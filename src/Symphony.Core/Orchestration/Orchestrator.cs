using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Core.Orchestration;

/// <summary>
/// Single-authority scheduler (SPEC 7, 8, 16). All state mutations happen on one message loop;
/// workers, timers and the HTTP surface only post messages. The loop never awaits tracker I/O:
/// tick and retry reads run as background fetches whose results come back as messages.
/// </summary>
public sealed class Orchestrator
{
    public delegate Task WorkerRunner(
        Issue issue,
        int? attempt,
        string? resumeSessionId,
        EffectiveWorkflow workflow,
        Action<AgentEvent> onEvent,
        Action<string> onWorkspace,
        CancellationToken ct);

    private const int ContinuationDelayMs = 1000;
    private const int RecentEventLimit = 50;

    private abstract record Msg;
    private sealed record TickMsg : Msg;
    private sealed record WorkerExitedMsg(string IssueId, Guid RunId, Exception? Error) : Msg;
    private sealed record AgentUpdateMsg(string IssueId, Guid RunId, AgentEvent Event) : Msg;
    private sealed record WorkspaceMsg(string IssueId, Guid RunId, string Path) : Msg;
    private sealed record RetryDueMsg(string IssueId, Guid Token) : Msg;
    private sealed record TickFetchedMsg(TickFetch Fetch) : Msg;
    private sealed record RetryFetchedMsg(RetryEntry Retry, WorkflowLease Lease, IReadOnlyList<Issue>? Refreshed, Exception? Error) : Msg;

    /// <summary>One tick's tracker reads, gathered off the loop. Owns the snapshot lease until applied.</summary>
    private sealed class TickFetch
    {
        public required WorkflowLease Lease { get; init; }
        /// <summary>Issue id -> run id of the live runs at tick start; results only apply to those runs.</summary>
        public required IReadOnlyDictionary<string, Guid> Runs { get; init; }
        /// <summary>Released issues whose handoff row is still open; refreshed in the same id fetch as the runs.</summary>
        public required HashSet<string> HandoffIds { get; init; }
        public required bool DispatchBlocked { get; init; }
        public IReadOnlyList<Issue>? Refreshed { get; set; }
        public Exception? RefreshError { get; set; }
        public IReadOnlyList<Issue>? Candidates { get; set; }
        public Exception? CandidatesError { get; set; }
    }

    private enum StopKind { Release, Stalled, Shutdown, IssueBudget, DayBudget }

    private sealed record StopDisposition(StopKind Kind, bool CleanupWorkspace, string Reason);

    private sealed class RunningEntry
    {
        public required Guid RunId { get; init; }
        public required Issue Issue { get; set; }
        public required int? RetryAttempt { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required WorkflowLease Lease { get; init; }
        /// <summary>The state and model the session was started for; <see cref="Issue"/> follows the tracker afterwards.</summary>
        public required string DispatchState { get; init; }
        public string? Model { get; init; }
        public bool Resumed { get; init; }
        public int TurnsCompleted { get; set; }
        public Task? Worker { get; set; }
        public string? WorkspacePath { get; set; }
        public string? ProcessId { get; set; }
        public string? ThreadId { get; set; }
        public string? TurnId { get; set; }
        public string? SessionId => ThreadId is null || TurnId is null ? null : $"{ThreadId}-{TurnId}";
        public string? LastEvent { get; set; }
        public DateTimeOffset? LastEventAt { get; set; }
        public string? LastMessage { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public long LastReportedInput { get; set; }
        public long LastReportedOutput { get; set; }
        public long LastReportedTotal { get; set; }
        public int TurnCount { get; set; }
        public StopDisposition? Stop { get; set; }
    }

    private sealed class RetryEntry
    {
        public required string IssueId { get; init; }
        public required string Identifier { get; init; }
        public string? Url { get; init; }
        public required int Attempt { get; init; }
        public required DateTimeOffset DueAt { get; init; }
        public string? Error { get; init; }
        public required Guid Token { get; init; }
        public required ITimer Timer { get; init; }
        /// <summary>Waiting for the daily token budget, not for a backoff.</summary>
        public bool Budget { get; init; }
    }

    private sealed class IssueTrack
    {
        public required string IssueId { get; init; }
        public required string Identifier { get; set; }
        public string? WorkspacePath { get; set; }
        public int RestartCount { get; set; }
        public string? LastError { get; set; }
        public int CleanExitsInState { get; set; }
        public string? CleanExitState { get; set; }
        public string? ResumeSessionId { get; set; }
        public string? ResumeState { get; set; }
        public string? ResumeModel { get; set; }
        public Queue<RecentEvent> Events { get; } = new();
    }

    private sealed record PublishedState(
        IReadOnlyList<RunningRow> Running,
        IReadOnlyList<RetryRow> Retrying,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        double EndedSeconds,
        IReadOnlyList<DateTimeOffset> ActiveStarts,
        JsonNode? RateLimits,
        string? WorkflowError,
        IReadOnlyDictionary<string, IssueDetail> Issues,
        IReadOnlyList<CandidateRow> Queue,
        PollInfo LastPoll,
        IReadOnlyList<HandoffRow> Handoffs,
        BudgetStatus Budget);

    private readonly WorkflowStore _store;
    private readonly WorkspaceManager _workspaces;
    private readonly WorkerRunner _runWorker;
    private readonly TimeProvider _time;
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<Msg> _inbox = Channel.CreateUnbounded<Msg>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, RunningEntry> _running = new();
    private readonly HashSet<string> _claimed = new();
    private readonly Dictionary<string, RetryEntry> _retries = new();
    // Due retries whose tracker refresh is in flight: no longer in _retries, not yet running.
    private readonly Dictionary<string, RetryEntry> _refreshing = new();
    private readonly HashSet<string> _completed = new();
    private readonly Dictionary<string, IssueTrack> _tracks = new();
    private readonly List<Task> _cleanups = new();
    private readonly List<Task> _fetches = new();
    private long _inputTokens;
    private long _outputTokens;
    private long _totalTokens;
    private double _endedSeconds;
    private JsonNode? _rateLimits;
    private ITimer? _tickTimer;
    private int _tickQueued;
    private int _nextTickMinDelayMs;
    private bool _tickInFlight;
    private bool _tickAgain;
    private volatile PublishedState? _published;

    // The last successful candidate poll, kept so the queue view can explain each candidate between polls.
    private IReadOnlyList<Issue> _candidates = [];
    private ServiceConfig? _candidatesConfig;
    private DateTimeOffset? _lastPollAt;
    private string? _lastPollError;

    private const int HandoffLimit = 20;
    private readonly List<HandoffRow> _handoffs = new();

    private readonly OrchestratorStateFile _stateFile;
    private readonly MetricsLog _metrics;

    // Tokens used on the host-local day, for budget.max_tokens_per_day.
    private DateOnly _usageDay;
    private long _usageTokens;

    public Orchestrator(
        WorkflowStore store,
        WorkspaceManager workspaces,
        WorkerRunner runWorker,
        TimeProvider time,
        ILogger<Orchestrator> logger)
    {
        _store = store;
        _workspaces = workspaces;
        _runWorker = runWorker;
        _time = time;
        _logger = logger;
        _stateFile = new OrchestratorStateFile(logger);
        _metrics = new MetricsLog(logger);
        _store.Reloaded += _ => RequestRefresh();
    }

    /// <summary>Bookkeeping only; not used for dispatch gating.</summary>
    public int CompletedCount => _completed.Count;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await StartupTerminalCleanupAsync(stoppingToken);
        RestoreState();
        _metrics.Load(MetricsPath);
        _tickTimer = _time.CreateTimer(_ => RequestRefresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        RequestRefresh();
        Publish();

        try
        {
            await foreach (var msg in _inbox.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    Handle(msg, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "action=orchestrator outcome=failed message={Message} reason={Reason}", msg.GetType().Name, ex.Message);
                }

                Publish();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await ShutdownAsync();
    }

    /// <summary>Queues an immediate poll + reconcile; repeated requests coalesce (SPEC 13.7.2).</summary>
    public RefreshResult RequestRefresh()
    {
        var coalesced = Interlocked.Exchange(ref _tickQueued, 1) == 1;
        if (!coalesced)
        {
            _inbox.Writer.TryWrite(new TickMsg());
        }

        return new RefreshResult(true, coalesced, _time.GetUtcNow());
    }

    /// <summary>Snapshot built from the last published state; never blocks on the loop.</summary>
    public RuntimeSnapshot GetSnapshot()
    {
        var p = _published ?? throw new InvalidOperationException("unavailable");
        var now = _time.GetUtcNow();
        var seconds = p.EndedSeconds + p.ActiveStarts.Sum(s => Math.Max(0, (now - s).TotalSeconds));
        return new RuntimeSnapshot(
            now,
            p.Running,
            p.Retrying,
            new AgentTotals(p.InputTokens, p.OutputTokens, p.TotalTokens, Math.Round(seconds, 1)),
            p.RateLimits,
            p.WorkflowError,
            p.Issues,
            p.Queue,
            p.LastPoll,
            p.Handoffs,
            p.Budget);
    }

    /// <summary>Run history and budget use (extension); computed on the caller's thread from the metrics log.</summary>
    public MetricsReport GetMetrics() =>
        _metrics.Report(_time.GetUtcNow(), _time.LocalTimeZone, _published?.Budget);

    private void Handle(Msg msg, CancellationToken ct)
    {
        switch (msg)
        {
            case TickMsg: OnTick(ct); break;
            case TickFetchedMsg m: OnTickFetched(m); break;
            case WorkerExitedMsg m: OnWorkerExited(m); break;
            case AgentUpdateMsg m: OnAgentUpdate(m); break;
            case WorkspaceMsg m: OnWorkspace(m); break;
            case RetryDueMsg m: OnRetryDue(m, ct); break;
            case RetryFetchedMsg m: OnRetryFetched(m); break;
        }
    }

    private void Post(Msg msg) => _inbox.Writer.TryWrite(msg);

    /// <summary>Hands a fetch result to the loop; after shutdown nobody will apply it, so release its lease.</summary>
    private void PostOrRelease(Msg msg, WorkflowLease lease)
    {
        if (!_inbox.Writer.TryWrite(msg))
        {
            lease.Dispose();
        }
    }

    private void StartFetch(Func<Task> fetch)
    {
        _fetches.RemoveAll(t => t.IsCompleted);
        _fetches.Add(Task.Run(fetch));
    }

    // ---- Tick (SPEC 8.1, 16.2) -------------------------------------------------------------

    private void OnTick(CancellationToken ct)
    {
        Interlocked.Exchange(ref _tickQueued, 0);
        if (_tickInFlight)
        {
            // Run one more tick once the current fetch lands (coalesced, SPEC 13.7.2).
            _tickAgain = true;
            return;
        }

        _store.ReloadIfChanged();
        var lease = _store.Acquire();
        try
        {
            ReconcileStalled(lease.Workflow.Config);

            var error = _store.LastError;
            if (error is not null)
            {
                _logger.LogError("action=dispatch outcome=skipped reason=invalid_workflow code={Code} error={Error}", error.Code, error.Message);
            }

            var fetch = new TickFetch
            {
                Lease = lease,
                Runs = _running.Values.Where(e => e.Stop is null).ToDictionary(e => e.Issue.Id, e => e.RunId),
                HandoffIds = _handoffs.Where(h => !h.Closed && !_claimed.Contains(h.IssueId)).Select(h => h.IssueId).ToHashSet(),
                DispatchBlocked = error is not null,
            };
            StartFetch(() => FetchForTickAsync(fetch, ct));
            _tickInFlight = true;
        }
        catch
        {
            // No fetch will land to re-arm the timer; keep polling alive.
            lease.Dispose();
            ArmTickTimer();
            throw;
        }
    }

    /// <summary>Runs off the loop. Reconcile refresh first, then candidates, as in SPEC 16.2.</summary>
    private async Task FetchForTickAsync(TickFetch fetch, CancellationToken ct)
    {
        var workflow = fetch.Lease.Workflow;
        var ids = fetch.Runs.Keys.Union(fetch.HandoffIds).ToList();
        if (ids.Count > 0)
        {
            try
            {
                fetch.Refreshed = await workflow.Tracker.FetchIssuesByIdsAsync(ids, ct);
            }
            catch (Exception ex)
            {
                fetch.RefreshError = ex;
            }
        }

        if (!fetch.DispatchBlocked && !ct.IsCancellationRequested)
        {
            try
            {
                fetch.Candidates = await workflow.Tracker.FetchIssuesByStatesAsync(workflow.Config.Tracker.ActiveStates, ct);
            }
            catch (Exception ex)
            {
                fetch.CandidatesError = ex;
            }
        }

        PostOrRelease(new TickFetchedMsg(fetch), fetch.Lease);
    }

    private void OnTickFetched(TickFetchedMsg m)
    {
        var fetch = m.Fetch;
        using var lease = fetch.Lease;
        _tickInFlight = false;
        var workflow = lease.Workflow;
        var config = workflow.Config;

        try
        {
            ApplyReconcile(fetch, config);
            RefreshHandoffs(fetch, config);
            ReleaseBudgetWaits(config);

            if (fetch.DispatchBlocked || _store.LastError is not null)
            {
                return;
            }

            if (fetch.CandidatesError is { } ex)
            {
                _lastPollError = ex.Message;
                _logger.LogWarning("action=fetch_candidates outcome=failed reason={Reason} retry_after_ms={RetryAfterMs}", ex.Message, NoteRetryAfter(ex));
                return;
            }

            _candidates = Eligibility.SortForDispatch(fetch.Candidates ?? []).ToList();
            _candidatesConfig = config;
            _lastPollAt = _time.GetUtcNow();
            _lastPollError = null;

            foreach (var issue in _candidates)
            {
                if (AvailableSlots(config) <= 0)
                {
                    break;
                }

                if (ShouldDispatch(issue, config))
                {
                    Dispatch(issue, workflow, attempt: null);
                }
            }
        }
        finally
        {
            ArmTickTimer();
            if (_tickAgain)
            {
                _tickAgain = false;
                RequestRefresh();
            }
        }
    }

    private void ArmTickTimer()
    {
        var delayMs = Math.Max(_store.Current.Config.PollIntervalMs, _nextTickMinDelayMs);
        _nextTickMinDelayMs = 0;
        _tickTimer?.Change(TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);
    }

    // ---- Reconciliation (SPEC 8.5, 16.3) -----------------------------------------------------

    private void ApplyReconcile(TickFetch fetch, ServiceConfig config)
    {
        if (fetch.RefreshError is { } ex)
        {
            _logger.LogWarning("action=reconcile outcome=failed reason={Reason} retry_after_ms={RetryAfterMs} note=keeping_workers", ex.Message, NoteRetryAfter(ex));
            return;
        }

        if (fetch.Refreshed is not { } refreshed)
        {
            return;
        }

        var returned = new HashSet<string>();
        foreach (var issue in refreshed)
        {
            returned.Add(issue.Id);
            if (!TryGetFetchedRun(fetch, issue.Id, out var entry))
            {
                continue;
            }

            entry.Issue = issue;
            if (config.IsTerminalState(issue.State))
            {
                Terminate(entry, cleanupWorkspace: true, $"terminal_state:{issue.State}");
            }
            else if (!config.IsActiveState(issue.State) || !Eligibility.IsRoutable(issue, config))
            {
                Terminate(entry, cleanupWorkspace: false, $"not_active_or_unroutable:{issue.State}");
            }
        }

        foreach (var missing in fetch.Runs.Keys.Where(id => !returned.Contains(id)))
        {
            if (TryGetFetchedRun(fetch, missing, out var entry))
            {
                Terminate(entry, cleanupWorkspace: false, "not_visible");
            }
        }
    }

    /// <summary>The still-live run the fetch was made for; a newer run of the same issue is left alone.</summary>
    private bool TryGetFetchedRun(TickFetch fetch, string issueId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RunningEntry? entry)
    {
        if (fetch.Runs.TryGetValue(issueId, out var runId)
            && _running.TryGetValue(issueId, out entry)
            && entry.RunId == runId
            && entry.Stop is null)
        {
            return true;
        }

        entry = null;
        return false;
    }

    private void ReconcileStalled(ServiceConfig config)
    {
        var stallMs = config.Claude.StallTimeoutMs;
        if (stallMs <= 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var entry in _running.Values.Where(e => e.Stop is null))
        {
            var since = entry.LastEventAt ?? entry.StartedAt;
            if ((now - since).TotalMilliseconds > stallMs)
            {
                entry.Stop = new StopDisposition(StopKind.Stalled, false, $"stalled: no agent activity for {stallMs} ms");
                _logger.LogWarning("action=stall_detected outcome=terminating issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId}",
                    entry.Issue.Id, entry.Issue.Identifier, entry.SessionId);
                entry.Cts.Cancel();
            }
        }
    }

    private void Terminate(RunningEntry entry, bool cleanupWorkspace, string reason)
    {
        entry.Stop = new StopDisposition(StopKind.Release, cleanupWorkspace, reason);
        _logger.LogInformation("action=terminate outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason} cleanup={Cleanup}",
            entry.Issue.Id, entry.Issue.Identifier, entry.SessionId, reason, cleanupWorkspace);
        entry.Cts.Cancel();
    }

    // ---- Dispatch (SPEC 8.2, 8.3, 16.4) ------------------------------------------------------

    private int AvailableSlots(ServiceConfig config) => Math.Max(config.Agent.MaxConcurrentAgents - _running.Count, 0);

    private bool StateSlotAvailable(string state, ServiceConfig config)
    {
        var key = StateNames.Normalize(state);
        var limit = config.Agent.MaxConcurrentAgentsByState.TryGetValue(key, out var l) ? l : config.Agent.MaxConcurrentAgents;
        return _running.Values.Count(e => StateNames.Normalize(e.Issue.State) == key) < limit;
    }

    private bool HasSlotFor(Issue issue, ServiceConfig config) =>
        AvailableSlots(config) > 0 && StateSlotAvailable(issue.State, config);

    private bool ShouldDispatch(Issue issue, ServiceConfig config) =>
        Eligibility.HasRequiredFields(issue)
        && config.IsActiveState(issue.State)
        && Eligibility.IsRoutable(issue, config)
        && !_running.ContainsKey(issue.Id)
        && !_claimed.Contains(issue.Id)
        && BudgetBlock(issue.Id, config) is null
        && HasSlotFor(issue, config);

    private void Dispatch(Issue issue, EffectiveWorkflow workflow, int? attempt)
    {
        var track = Track(issue);
        var model = workflow.Config.Claude.ModelFor(issue.State);
        var resume = ResumeFor(track, issue, workflow.Config, model);
        var entry = new RunningEntry
        {
            RunId = Guid.NewGuid(),
            Issue = issue,
            RetryAttempt = attempt,
            StartedAt = _time.GetUtcNow(),
            Cts = new CancellationTokenSource(),
            Lease = _store.Acquire(workflow),
            DispatchState = issue.State,
            Model = model,
            Resumed = resume is not null,
        };

        _running[issue.Id] = entry;
        _claimed.Add(issue.Id);
        CancelRetry(issue.Id);

        if (attempt is not null)
        {
            track.RestartCount++;
        }

        _logger.LogInformation("action=dispatch outcome=started issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} attempt={Attempt} resume_session={Resume}",
            issue.Id, issue.Identifier, issue.State, attempt, resume);

        var id = issue.Id;
        var runId = entry.RunId;
        var token = entry.Cts.Token;
        entry.Worker = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await _runWorker(
                    issue,
                    attempt,
                    resume,
                    workflow,
                    ev => Post(new AgentUpdateMsg(id, runId, ev)),
                    path => Post(new WorkspaceMsg(id, runId, path)),
                    token);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Post(new WorkerExitedMsg(id, runId, error));
        });
    }

    /// <summary>
    /// The session to continue (<c>claude.resume</c>): the issue's last one, when it ran in the same state with
    /// the same model. A session that keeps ending without moving the issue gets a fresh start instead.
    /// </summary>
    private static string? ResumeFor(IssueTrack track, Issue issue, ServiceConfig config, string? model) =>
        config.Claude.Resume
        && track.ResumeSessionId is { } id
        && track.ResumeState == StateNames.Normalize(issue.State)
        && track.ResumeModel == model
        && track.CleanExitsInState <= config.Agent.MaxFastContinuations
            ? id
            : null;

    // ---- Worker events (SPEC 7.3, 16.6) ------------------------------------------------------

    private void OnWorkspace(WorkspaceMsg m)
    {
        if (_running.TryGetValue(m.IssueId, out var entry) && entry.RunId == m.RunId)
        {
            entry.WorkspacePath = m.Path;
            if (_tracks.TryGetValue(m.IssueId, out var track))
            {
                track.WorkspacePath = m.Path;
            }
        }
    }

    private void OnAgentUpdate(AgentUpdateMsg m)
    {
        if (!_running.TryGetValue(m.IssueId, out var entry) || entry.RunId != m.RunId)
        {
            return;
        }

        var ev = m.Event;
        entry.LastEvent = ev.Event;
        entry.LastEventAt = ev.Timestamp;
        if (ev.Message is not null) entry.LastMessage = Truncate(ev.Message, 500);
        if (ev.ProcessId is not null) entry.ProcessId = ev.ProcessId;
        if (ev.ThreadId is not null) entry.ThreadId = ev.ThreadId;
        if (ev.TurnId is not null) entry.TurnId = ev.TurnId;
        if (ev.Event == AgentEventNames.TurnStarted) entry.TurnCount++;
        if (ev.Event == AgentEventNames.TurnCompleted) entry.TurnsCompleted++;
        if (ev.RateLimits is not null) _rateLimits = ev.RateLimits.DeepClone();
        if (ev.Gate is { } gate)
        {
            _metrics.Add(MetricsPath, new MetricRecord(
                MetricRecord.Gate, m.IssueId, entry.Issue.Identifier, ev.Timestamp, gate.State,
                Seconds: gate.Seconds,
                Outcome: gate.Passed ? MetricRecord.GatePassed : gate.Category ?? "failed",
                FromState: gate.FromState));
        }
        if (ev.ThreadId is not null && _tracks.TryGetValue(m.IssueId, out var resumable))
        {
            resumable.ResumeSessionId = ev.ThreadId;
            resumable.ResumeState = StateNames.Normalize(entry.DispatchState);
            resumable.ResumeModel = entry.Model;
        }

        if (ev.AbsoluteTotals is { } totals)
        {
            // Absolute totals: account only the delta since the last report (SPEC 13.5).
            var dIn = Math.Max(0, totals.InputTokens - entry.LastReportedInput);
            var dOut = Math.Max(0, totals.OutputTokens - entry.LastReportedOutput);
            var dTotal = Math.Max(0, totals.TotalTokens - entry.LastReportedTotal);
            entry.LastReportedInput = Math.Max(entry.LastReportedInput, totals.InputTokens);
            entry.LastReportedOutput = Math.Max(entry.LastReportedOutput, totals.OutputTokens);
            entry.LastReportedTotal = Math.Max(entry.LastReportedTotal, totals.TotalTokens);
            entry.InputTokens += dIn;
            entry.OutputTokens += dOut;
            entry.TotalTokens += dTotal;
            _inputTokens += dIn;
            _outputTokens += dOut;
            _totalTokens += dTotal;
            RollUsageDay();
            _usageTokens += dTotal;
            EnforceBudget(entry);
        }

        if (ev.Event is AgentEventNames.SessionStarted or AgentEventNames.TurnCompleted or AgentEventNames.TurnFailed or AgentEventNames.StartupFailed)
        {
            _logger.LogInformation("action=agent_event event={Event} issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} pid={Pid}",
                ev.Event, entry.Issue.Id, entry.Issue.Identifier, entry.SessionId, entry.ProcessId);
        }

        if (_tracks.TryGetValue(m.IssueId, out var track) && ev.Event is not AgentEventNames.RateLimit)
        {
            track.Events.Enqueue(new RecentEvent(ev.Timestamp, ev.Event, ev.Message is null ? null : Truncate(ev.Message, 300)));
            while (track.Events.Count > RecentEventLimit) track.Events.Dequeue();
        }
    }

    private void OnWorkerExited(WorkerExitedMsg m)
    {
        if (!_running.TryGetValue(m.IssueId, out var entry) || entry.RunId != m.RunId)
        {
            return;
        }

        _running.Remove(m.IssueId);
        entry.Cts.Dispose();
        entry.Lease.Dispose();
        _endedSeconds += Math.Max(0, (_time.GetUtcNow() - entry.StartedAt).TotalSeconds);
        var issue = entry.Issue;
        var nextAttempt = (entry.RetryAttempt ?? 0) + 1;
        RecordRun(entry, entry.Stop?.Kind switch
        {
            StopKind.Release => RunOutcomes.Released,
            StopKind.Stalled => RunOutcomes.Stalled,
            StopKind.Shutdown => RunOutcomes.Interrupted,
            StopKind.IssueBudget or StopKind.DayBudget => RunOutcomes.Budget,
            _ => m.Error is null ? RunOutcomes.Completed : RunOutcomes.Failed,
        }, entry.Stop?.Reason ?? (m.Error is null ? null : Describe(m.Error)));

        switch (entry.Stop?.Kind)
        {
            case StopKind.IssueBudget:
                // Not retried: the queue shows it as paused until the budget is raised.
                _logger.LogWarning("action=worker_exit outcome=paused issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
                    issue.Id, issue.Identifier, entry.SessionId, entry.Stop.Reason);
                Release(issue.Id);
                PostComment(issue, $"Symphony paused this task: {entry.Stop.Reason}. Raise `budget.max_tokens_per_issue` in WORKFLOW.md to let the agents continue.");
                return;

            case StopKind.DayBudget:
                _logger.LogWarning("action=worker_exit outcome=paused issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
                    issue.Id, issue.Identifier, entry.SessionId, entry.Stop.Reason);
                SetLastError(issue.Id, entry.Stop.Reason);
                WaitForDayBudget(issue.Id, entry.RetryAttempt ?? 1, issue.Identifier, issue.Url, entry.Stop.Reason);
                PostComment(issue, $"Symphony paused work on this task until tomorrow: {entry.Stop.Reason}. Raise `budget.max_tokens_per_day` in WORKFLOW.md to continue today.");
                return;

            case StopKind.Release:
                _logger.LogInformation("action=worker_exit outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
                    issue.Id, issue.Identifier, entry.SessionId, entry.Stop.Reason);
                Release(issue.Id);
                RecordHandoff(issue.Id, issue.Identifier, issue.Url, entry.Stop.Reason == "not_visible" ? null : issue);
                if (entry.Stop.CleanupWorkspace)
                {
                    StartCleanup(issue.Identifier);
                }

                return;

            case StopKind.Stalled:
                SetLastError(issue.Id, entry.Stop.Reason);
                ScheduleRetry(issue.Id, nextAttempt, issue.Identifier, issue.Url, entry.Stop.Reason, continuation: false);
                return;

            case StopKind.Shutdown:
                _claimed.Remove(issue.Id);
                return;
        }

        if (m.Error is null)
        {
            _completed.Add(issue.Id);
            _logger.LogInformation("action=worker_exit outcome=completed issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId}",
                issue.Id, issue.Identifier, entry.SessionId);
            ScheduleContinuation(issue);
        }
        else
        {
            var reason = $"worker exited: {Describe(m.Error)}";
            SetLastError(issue.Id, reason);
            if (entry.Resumed && entry.TurnsCompleted == 0 && _tracks.TryGetValue(issue.Id, out var failed))
            {
                // Resuming got nowhere; the retry starts a new session.
                failed.ResumeSessionId = null;
            }

            _logger.LogWarning("action=worker_exit outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
                issue.Id, issue.Identifier, entry.SessionId, reason);
            ScheduleRetry(issue.Id, nextAttempt, issue.Identifier, issue.Url, reason, continuation: false);
        }
    }

    // ---- Retry (SPEC 8.4, 16.6) --------------------------------------------------------------

    public static int BackoffMs(int attempt, int maxBackoffMs)
    {
        var exp = Math.Clamp(attempt - 1, 0, 30);
        var delay = 10000d * Math.Pow(2, exp);
        return (int)Math.Min(delay, maxBackoffMs);
    }

    /// <summary>
    /// SPEC 7.1 continuation after a clean exit. Sessions that keep ending with the issue in the same
    /// state make no tracker progress, so past <c>agent.max_fast_continuations</c> they back off.
    /// </summary>
    private void ScheduleContinuation(Issue issue)
    {
        var track = Track(issue);
        var state = StateNames.Normalize(issue.State);
        track.CleanExitsInState = track.CleanExitState == state ? track.CleanExitsInState + 1 : 1;
        track.CleanExitState = state;

        var agent = _store.Current.Config.Agent;
        var slowed = track.CleanExitsInState - agent.MaxFastContinuations;
        if (slowed <= 0)
        {
            ScheduleRetry(issue.Id, 1, issue.Identifier, issue.Url, error: null, continuation: true);
            return;
        }

        ScheduleRetry(issue.Id, 1, issue.Identifier, issue.Url,
            $"no state change after {track.CleanExitsInState} sessions in {issue.State}; backing off",
            continuation: true, BackoffMs(slowed, agent.MaxRetryBackoffMs));
    }

    private void ScheduleRetry(string issueId, int attempt, string identifier, string? url, string? error, bool continuation, int minDelayMs = 0)
    {
        var delayMs = Math.Max(minDelayMs, continuation ? ContinuationDelayMs : BackoffMs(attempt, _store.Current.Config.Agent.MaxRetryBackoffMs));
        ArmRetry(issueId, attempt, identifier, url, error, delayMs, continuation ? "continuation" : "failure");
    }

    private void ArmRetry(string issueId, int attempt, string identifier, string? url, string? error, int delayMs, string kind, bool budget = false)
    {
        CancelRetry(issueId);
        var token = Guid.NewGuid();
        var timer = _time.CreateTimer(_ => Post(new RetryDueMsg(issueId, token)), null, TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);
        _retries[issueId] = new RetryEntry
        {
            IssueId = issueId,
            Identifier = identifier,
            Url = url,
            Attempt = attempt,
            DueAt = _time.GetUtcNow().AddMilliseconds(delayMs),
            Error = error,
            Token = token,
            Timer = timer,
            Budget = budget,
        };
        _claimed.Add(issueId);
        _logger.LogInformation("action=retry outcome=retrying issue_id={IssueId} issue_identifier={IssueIdentifier} attempt={Attempt} delay_ms={DelayMs} kind={Kind} error={Error}",
            issueId, identifier, attempt, delayMs, kind, error);
    }

    private void CancelRetry(string issueId)
    {
        if (_retries.Remove(issueId, out var existing))
        {
            existing.Timer.Dispose();
        }
    }

    private void OnRetryDue(RetryDueMsg m, CancellationToken ct)
    {
        if (!_retries.TryGetValue(m.IssueId, out var retry) || retry.Token != m.Token)
        {
            return;
        }

        // The claim stays while the refresh is in flight, so ticks will not dispatch the issue.
        _retries.Remove(m.IssueId);
        retry.Timer.Dispose();
        _refreshing[m.IssueId] = retry;
        var lease = _store.Acquire();
        StartFetch(async () =>
        {
            IReadOnlyList<Issue>? refreshed = null;
            Exception? error = null;
            try
            {
                refreshed = await lease.Workflow.Tracker.FetchIssuesByIdsAsync([retry.IssueId], ct);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            PostOrRelease(new RetryFetchedMsg(retry, lease, refreshed, error), lease);
        });
    }

    private void OnRetryFetched(RetryFetchedMsg m)
    {
        using var lease = m.Lease;
        var retry = m.Retry;
        if (_refreshing.TryGetValue(retry.IssueId, out var pending) && pending.Token == retry.Token)
        {
            _refreshing.Remove(retry.IssueId);
        }

        var workflow = lease.Workflow;
        var config = workflow.Config;
        if (!_claimed.Contains(retry.IssueId) || _running.ContainsKey(retry.IssueId) || _retries.ContainsKey(retry.IssueId))
        {
            return; // released or taken over while the refresh was in flight
        }

        if (m.Error is { } ex)
        {
            ScheduleRetry(retry.IssueId, retry.Attempt + 1, retry.Identifier, retry.Url, $"retry refresh failed: {ex.Message}", continuation: false, RetryAfterMs(ex));
            return;
        }

        var issue = m.Refreshed!.FirstOrDefault(i => i.Id == retry.IssueId);
        if (issue is null)
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=not_visible", retry.IssueId, retry.Identifier);
            Release(retry.IssueId);
            RecordHandoff(retry.IssueId, retry.Identifier, retry.Url, null);
            return;
        }

        if (config.IsTerminalState(issue.State))
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=terminal_state state={State}", issue.Id, issue.Identifier, issue.State);
            Release(issue.Id);
            RecordHandoff(issue.Id, issue.Identifier, issue.Url, issue);
            StartCleanup(issue.Identifier);
            return;
        }

        if (!Eligibility.HasRequiredFields(issue) || !config.IsActiveState(issue.State) || !Eligibility.IsRoutable(issue, config))
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=not_active_or_unroutable state={State}", issue.Id, issue.Identifier, issue.State);
            Release(issue.Id);
            RecordHandoff(issue.Id, issue.Identifier, issue.Url, issue);
            return;
        }

        if (_store.LastError is not null)
        {
            ScheduleRetry(issue.Id, retry.Attempt + 1, issue.Identifier, issue.Url, "workflow invalid; dispatch blocked", continuation: false);
            return;
        }

        if (IssueBudgetBlock(issue.Id, config) is { } overIssue)
        {
            _logger.LogWarning("action=retry outcome=paused issue_id={IssueId} issue_identifier={IssueIdentifier} reason={Reason}", issue.Id, issue.Identifier, overIssue);
            Release(issue.Id);
            return;
        }

        if (DayBudgetBlock(config) is { } overDay)
        {
            WaitForDayBudget(issue.Id, retry.Attempt, issue.Identifier, issue.Url, overDay);
            return;
        }

        if (!HasSlotFor(issue, config))
        {
            ScheduleRetry(issue.Id, retry.Attempt + 1, issue.Identifier, issue.Url, "no available orchestrator slots", continuation: false);
            return;
        }

        Dispatch(issue, workflow, retry.Attempt);
    }

    // ---- Token budget (extension) ------------------------------------------------------------

    private void RollUsageDay()
    {
        var today = MetricsLog.Day(_time.GetUtcNow(), _time.LocalTimeZone);
        if (today != _usageDay)
        {
            _usageDay = today;
            _usageTokens = 0;
        }
    }

    private BudgetStatus BudgetNow(ServiceConfig config)
    {
        RollUsageDay();
        return new BudgetStatus(_usageDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _usageTokens, config.Budgets.MaxTokensPerDay, config.Budgets.MaxTokensPerIssue);
    }

    /// <summary>Why the issue's own budget stops it, counting finished runs and the live one; null when it doesn't.</summary>
    private string? IssueBudgetBlock(string issueId, ServiceConfig config)
    {
        var cap = config.Budgets.MaxTokensPerIssue;
        if (cap <= 0)
        {
            return null;
        }

        var used = _metrics.IssueTokens(issueId) + (_running.TryGetValue(issueId, out var live) ? live.TotalTokens : 0);
        return used >= cap ? $"it has used {used:N0} tokens, reaching the task budget of {cap:N0}" : null;
    }

    private string? DayBudgetBlock(ServiceConfig config)
    {
        var budget = BudgetNow(config);
        return budget.DayExceeded ? $"today's token budget of {budget.MaxTokensPerDay:N0} is used up ({budget.DayTokens:N0} tokens)" : null;
    }

    private string? BudgetBlock(string issueId, ServiceConfig config) =>
        IssueBudgetBlock(issueId, config) ?? DayBudgetBlock(config);

    /// <summary>Token use is reported when a turn ends, so a run over budget stops before its next turn gets far.</summary>
    private void EnforceBudget(RunningEntry entry)
    {
        var config = _store.Current.Config;
        if (entry.Stop is null && IssueBudgetBlock(entry.Issue.Id, config) is { } overIssue)
        {
            StopForBudget(entry, StopKind.IssueBudget, overIssue);
        }

        if (DayBudgetBlock(config) is { } overDay)
        {
            foreach (var running in _running.Values.Where(e => e.Stop is null))
            {
                StopForBudget(running, StopKind.DayBudget, overDay);
            }
        }
    }

    private void StopForBudget(RunningEntry entry, StopKind kind, string reason)
    {
        entry.Stop = new StopDisposition(kind, false, reason);
        _logger.LogWarning("action=budget outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
            entry.Issue.Id, entry.Issue.Identifier, entry.SessionId, reason);
        entry.Cts.Cancel();
    }

    /// <summary>Keeps the claim, and with it the session to resume, until the local day ends.</summary>
    private void WaitForDayBudget(string issueId, int attempt, string identifier, string? url, string reason)
    {
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
        var untilMidnight = local.Date.AddDays(1) - local.DateTime;
        ArmRetry(issueId, attempt, identifier, url, reason, (int)untilMidnight.TotalMilliseconds + 1000, "day_budget", budget: true);
    }

    /// <summary>A raised or removed daily cap lets the waiting issues go on at once.</summary>
    private void ReleaseBudgetWaits(ServiceConfig config)
    {
        if (DayBudgetBlock(config) is not null)
        {
            return;
        }

        foreach (var retry in _retries.Values.Where(r => r.Budget).ToList())
        {
            ArmRetry(retry.IssueId, retry.Attempt, retry.Identifier, retry.Url, retry.Error, 0, "day_budget_lifted");
        }
    }

    /// <summary>Tells the people on the task, through the tracker's own <c>add_comment</c> tool; best effort.</summary>
    private void PostComment(Issue issue, string text)
    {
        var lease = _store.Acquire();
        StartFetch(async () =>
        {
            using (lease)
            {
                try
                {
                    var result = await lease.Workflow.Tracker.ExecuteAgentToolAsync("add_comment", new JsonObject { ["text"] = text }, issue, CancellationToken.None);
                    if (!result.Success)
                    {
                        _logger.LogWarning("action=budget_comment outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} reason={Reason}",
                            issue.Id, issue.Identifier, result.Output?.ToJsonString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("action=budget_comment outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} reason={Reason}", issue.Id, issue.Identifier, ex.Message);
                }
            }
        });
    }

    // ---- Metrics (extension) -----------------------------------------------------------------

    private string MetricsPath => MetricsLog.PathFor(_store.Current.Config.WorkspaceRoot);

    private void RecordRun(RunningEntry e, string outcome, string? error)
    {
        var now = _time.GetUtcNow();
        _metrics.Add(MetricsPath, new MetricRecord(
            MetricRecord.Run,
            e.Issue.Id,
            e.Issue.Identifier,
            now,
            e.DispatchState,
            e.Model,
            e.StartedAt,
            Math.Round(Math.Max(0, (now - e.StartedAt).TotalSeconds), 1),
            e.TurnCount,
            e.InputTokens,
            e.OutputTokens,
            e.TotalTokens,
            e.RetryAttempt,
            outcome,
            error is null ? null : Truncate(error, 300),
            e.Resumed,
            _metrics.IsRework(e.Issue.Id, e.DispatchState)));
    }

    /// <summary>Tracker rate-limit hint (adapter extension, SPEC 11.4); 0 when absent.</summary>
    private static int RetryAfterMs(Exception ex) =>
        ex is TrackerException { RetryAfterMs: { } ms } && ms > 0 ? ms : 0;

    /// <summary>Pushes the next scheduled poll out to at least the tracker's Retry-After.</summary>
    private int NoteRetryAfter(Exception ex)
    {
        var ms = RetryAfterMs(ex);
        _nextTickMinDelayMs = Math.Max(_nextTickMinDelayMs, ms);
        return ms;
    }

    /// <summary>Remembers a release for the board; <paramref name="issue"/> is the refreshed record, null when it left scope.</summary>
    private void RecordHandoff(string issueId, string identifier, string? url, Issue? issue)
    {
        var config = _store.Current.Config;
        var reason = issue switch
        {
            null => "No longer visible to the tracker adapter",
            _ when config.IsTerminalState(issue.State) => $"Reached terminal state \"{issue.State}\"",
            _ when !config.IsActiveState(issue.State) => $"Moved to \"{issue.State}\"",
            { Dispatchable: false } => $"No longer eligible: {issue.HoldReason ?? "not dispatchable"}",
            _ => "No longer eligible: missing a required label",
        };

        // One row per issue: a newer release replaces the older one.
        var now = _time.GetUtcNow();
        var closed = issue is not null && config.IsTerminalState(issue.State);
        _metrics.Add(MetricsPath, new MetricRecord(closed ? MetricRecord.Closed : MetricRecord.Handoff, issueId, identifier, now, issue?.State));
        _handoffs.RemoveAll(h => h.IssueId == issueId);
        _handoffs.Insert(0, new HandoffRow(issueId, identifier, issue?.Url ?? url, issue?.Title, issue?.State, now, reason, issue?.State, closed, now));
        if (_handoffs.Count > HandoffLimit)
        {
            _handoffs.RemoveAt(_handoffs.Count - 1);
        }
    }

    /// <summary>
    /// Follows released issues in the tracker so the board shows their current state. A row closes when the
    /// issue reaches a terminal state, which also removes its workspace (SPEC 8.6 cleanup, otherwise only done
    /// at startup); an issue back in an active state leaves the list, since the queue explains it from then on.
    /// </summary>
    private void RefreshHandoffs(TickFetch fetch, ServiceConfig config)
    {
        if (fetch.HandoffIds.Count == 0 || fetch.RefreshError is not null || fetch.Refreshed is not { } refreshed)
        {
            return;
        }

        var byId = refreshed.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var now = _time.GetUtcNow();
        for (var i = _handoffs.Count - 1; i >= 0; i--)
        {
            var row = _handoffs[i];
            if (row.Closed || _claimed.Contains(row.IssueId) || !fetch.HandoffIds.Contains(row.IssueId))
            {
                continue;
            }

            if (!byId.TryGetValue(row.IssueId, out var issue))
            {
                _handoffs[i] = row with { State = null, CheckedAt = now };
                continue;
            }

            var terminal = config.IsTerminalState(issue.State);
            if (!terminal && config.IsActiveState(issue.State))
            {
                _logger.LogInformation("action=handoff outcome=reopened issue_id={IssueId} issue_identifier={IssueIdentifier} state={State}", issue.Id, issue.Identifier, issue.State);
                _handoffs.RemoveAt(i);
                continue;
            }

            _handoffs[i] = row with { IssueUrl = issue.Url ?? row.IssueUrl, Title = issue.Title, State = issue.State, Closed = terminal, CheckedAt = now };
            if (terminal)
            {
                _logger.LogInformation("action=handoff outcome=closed issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} note=removing_workspace", issue.Id, issue.Identifier, issue.State);
                _metrics.Add(MetricsPath, new MetricRecord(MetricRecord.Closed, issue.Id, issue.Identifier, now, issue.State));
                StartCleanup(issue.Identifier);
            }
        }
    }

    private void Release(string issueId)
    {
        _claimed.Remove(issueId);
        CancelRetry(issueId);
        _tracks.Remove(issueId);
    }

    // ---- Workspace cleanup (SPEC 8.6) --------------------------------------------------------

    private async Task StartupTerminalCleanupAsync(CancellationToken ct)
    {
        using var lease = _store.Acquire();
        var workflow = lease.Workflow;
        try
        {
            var terminal = await workflow.Tracker.FetchIssuesByStatesAsync(workflow.Config.Tracker.TerminalStates, ct);
            foreach (var issue in terminal)
            {
                await _workspaces.RemoveAsync(issue.Identifier, workflow.Config, ct);
            }

            _logger.LogInformation("action=startup_cleanup outcome=completed terminal_issues={Count}", terminal.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("action=startup_cleanup outcome=failed reason={Reason} note=continuing_startup", ex.Message);
        }
    }

    private void StartCleanup(string identifier)
    {
        var config = _store.Current.Config;
        _cleanups.RemoveAll(t => t.IsCompleted);
        _cleanups.Add(Task.Run(() => _workspaces.RemoveAsync(identifier, config, CancellationToken.None)));
    }

    // ---- Shutdown ----------------------------------------------------------------------------

    private async Task ShutdownAsync()
    {
        // Late worker and fetch posts now fail; fetches release their own leases (PostOrRelease).
        _inbox.Writer.TryComplete();
        _tickTimer?.Dispose();
        foreach (var retry in _retries.Values)
        {
            retry.Timer.Dispose();
        }

        var workers = new List<Task>();
        foreach (var entry in _running.Values)
        {
            entry.Stop ??= new StopDisposition(StopKind.Shutdown, false, "shutdown");
            // The loop no longer handles worker exits, so the run is recorded here.
            RecordRun(entry, RunOutcomes.Interrupted, "shutdown");
            entry.Cts.Cancel();
            if (entry.Worker is not null) workers.Add(entry.Worker);
        }

        var grace = TimeSpan.FromMilliseconds(_store.Current.Config.Hooks.TimeoutMs + 15000);
        try
        {
            await Task.WhenAll(workers.Concat(_cleanups).Concat(_fetches)).WaitAsync(grace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("action=shutdown outcome=incomplete reason={Reason}", ex.Message);
        }

        // Fetch results that arrived after the loop stopped still own leases.
        while (_inbox.Reader.TryRead(out var pending))
        {
            if (pending is TickFetchedMsg t) t.Fetch.Lease.Dispose();
            else if (pending is RetryFetchedMsg r) r.Lease.Dispose();
        }

        // Workers still running past the grace period keep their snapshot's adapter.
        foreach (var entry in _running.Values.Where(e => e.Worker is null || e.Worker.IsCompleted))
        {
            entry.Lease.Dispose();
        }

        _logger.LogInformation("action=shutdown outcome=completed stopped_workers={Count}", workers.Count);
    }

    // ---- State across restarts (SPEC 18.2 extension) -----------------------------------------

    /// <summary>
    /// Re-queues what the previous process had claimed. Queued retries keep their attempt and remaining
    /// backoff; a run that was live comes back as the next attempt after a short delay, so its prompt says to
    /// check the workspace. Each one is refreshed from the tracker before it can dispatch (SPEC 8.4).
    /// </summary>
    private void RestoreState()
    {
        var config = _store.Current.Config;
        var path = OrchestratorStateFile.PathFor(config.WorkspaceRoot);
        if (_stateFile.Load(path) is not { } saved)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var restored = 0;
        foreach (var s in saved.Issues)
        {
            if (string.IsNullOrEmpty(s.IssueId) || string.IsNullOrEmpty(s.Identifier) || _claimed.Contains(s.IssueId))
            {
                continue;
            }

            var track = new IssueTrack
            {
                IssueId = s.IssueId,
                Identifier = s.Identifier,
                WorkspacePath = s.WorkspacePath,
                RestartCount = s.RestartCount,
                LastError = s.LastError,
                CleanExitsInState = s.CleanExitsInState,
                CleanExitState = s.CleanExitState,
                ResumeSessionId = s.ResumeSessionId,
                ResumeState = s.ResumeState,
                ResumeModel = s.ResumeModel,
            };
            _tracks[s.IssueId] = track;

            if (s.Kind == SavedIssue.Interrupted)
            {
                const string reason = "interrupted: Symphony stopped while the agent was running";
                track.LastError = reason;
                ArmRetry(s.IssueId, (s.Attempt ?? 0) + 1, s.Identifier, s.Url, reason, ContinuationDelayMs, "restored_interrupted");
            }
            else
            {
                var remainingMs = s.DueAt is { } due ? Math.Clamp((due - now).TotalMilliseconds, 0, config.Agent.MaxRetryBackoffMs) : 0;
                ArmRetry(s.IssueId, Math.Max(1, s.Attempt ?? 1), s.Identifier, s.Url, s.Error, (int)remainingMs, "restored");
            }

            restored++;
        }

        _handoffs.AddRange(saved.Handoffs.Where(h => !_claimed.Contains(h.IssueId)).Take(HandoffLimit));
        if (saved.Usage is { } usage && DateOnly.TryParseExact(usage.Day, "yyyy-MM-dd", out var day))
        {
            _usageDay = day;
            _usageTokens = Math.Max(0, usage.Tokens);
        }

        _logger.LogInformation("action=state_restore outcome=completed path={Path} saved_at={SavedAt} issues={Issues} handoffs={Handoffs}",
            path, saved.SavedAt, restored, _handoffs.Count);
    }

    private void PersistState()
    {
        var issues = new List<SavedIssue>();
        foreach (var e in _running.Values.Where(e => e.Stop?.Kind != StopKind.Release))
        {
            issues.Add(Saved(e.Issue.Id, e.Issue.Identifier, e.Issue.Url, SavedIssue.Interrupted, e.RetryAttempt, null, null, e.Issue.State, e.SessionId));
        }

        foreach (var r in _retries.Values.Concat(_refreshing.Values).Where(r => !_running.ContainsKey(r.IssueId)))
        {
            var due = _refreshing.ContainsKey(r.IssueId) ? (DateTimeOffset?)null : r.DueAt;
            issues.Add(Saved(r.IssueId, r.Identifier, r.Url, SavedIssue.Retry, r.Attempt, due, r.Error, null, null));
        }

        issues.Sort((a, b) => string.CompareOrdinal(a.IssueId, b.IssueId));
        var usage = _usageTokens > 0 ? new SavedUsage(_usageDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _usageTokens) : null;
        var state = new OrchestratorState(OrchestratorState.CurrentVersion, _time.GetUtcNow(), issues, _handoffs.ToList(), usage);
        _stateFile.Save(OrchestratorStateFile.PathFor(_store.Current.Config.WorkspaceRoot), state);
    }

    private SavedIssue Saved(string issueId, string identifier, string? url, string kind, int? attempt, DateTimeOffset? dueAt, string? error, string? state, string? sessionId)
    {
        _tracks.TryGetValue(issueId, out var t);
        return new SavedIssue(issueId, identifier, url, kind, attempt, dueAt, error, state, sessionId,
            t?.WorkspacePath, t?.RestartCount ?? 0, t?.LastError, t?.CleanExitsInState ?? 0, t?.CleanExitState,
            t?.ResumeSessionId, t?.ResumeState, t?.ResumeModel);
    }

    // ---- Observability -----------------------------------------------------------------------

    private IssueTrack Track(Issue issue)
    {
        if (!_tracks.TryGetValue(issue.Id, out var track))
        {
            track = new IssueTrack { IssueId = issue.Id, Identifier = issue.Identifier };
            _tracks[issue.Id] = track;
        }

        track.Identifier = issue.Identifier;
        return track;
    }

    private void SetLastError(string issueId, string error)
    {
        if (_tracks.TryGetValue(issueId, out var track))
        {
            track.LastError = error;
        }
    }

    private void Publish()
    {
        var runningRows = _running.Values.Select(ToRow).ToList();
        var retryRows = _retries.Values.Select(r => new RetryRow(r.IssueId, r.Identifier, r.Url, r.Attempt, r.DueAt, r.Error)).ToList();
        var issues = new Dictionary<string, IssueDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _tracks.Values)
        {
            var row = runningRows.FirstOrDefault(r => r.IssueId == track.IssueId);
            var retry = retryRows.FirstOrDefault(r => r.IssueId == track.IssueId);
            var status = row is not null ? (row.Stopping is null ? "running" : "stopping") : retry is not null ? "retrying" : "claimed";
            issues[track.Identifier] = new IssueDetail(
                track.Identifier,
                track.IssueId,
                status,
                track.WorkspacePath,
                track.RestartCount,
                retry?.Attempt ?? (_running.TryGetValue(track.IssueId, out var e) ? e.RetryAttempt : null),
                row,
                retry,
                track.Events.ToList(),
                track.LastError);
        }

        _published = new PublishedState(
            runningRows,
            retryRows,
            _inputTokens,
            _outputTokens,
            _totalTokens,
            _endedSeconds,
            _running.Values.Select(e => e.StartedAt).ToList(),
            _rateLimits?.DeepClone(),
            _store.LastError is { } err ? $"{err.Code}: {err.Message}" : null,
            issues,
            _candidatesConfig is { } config ? _candidates.Where(i => !HandedOffSincePoll(i.Id)).Select(i => Explain(i, config)).ToList() : [],
            new PollInfo(_lastPollAt, _lastPollError, (_candidatesConfig ?? _store.Current.Config).Tracker.ActiveStates),
            _handoffs.ToList(),
            BudgetNow(_store.Current.Config));

        PersistState();
    }

    /// <summary>The last poll still lists an issue released after it; hide it until the next poll confirms.</summary>
    private bool HandedOffSincePoll(string issueId) =>
        _handoffs.Any(h => h.IssueId == issueId && h.At >= _lastPollAt) && !_running.ContainsKey(issueId) && !_retries.ContainsKey(issueId);

    /// <summary>The queue view: why a polled candidate is or isn't running, in the order dispatch tries them.</summary>
    private CandidateRow Explain(Issue issue, ServiceConfig config)
    {
        var (status, reason) = Why(issue, config);
        return new CandidateRow(issue.Id, issue.Identifier, issue.Url, issue.Title, issue.State, issue.Priority, status, reason);
    }

    private (string Status, string Reason) Why(Issue issue, ServiceConfig config)
    {
        if (_running.TryGetValue(issue.Id, out var run))
        {
            return ("running", run.Stop is null ? "An agent session is running" : $"Stopping: {run.Stop.Reason}");
        }

        if (_retries.TryGetValue(issue.Id, out var retry))
        {
            return ("retrying", retry.Error is null ? $"Continuation {retry.Attempt} is scheduled" : $"Retry {retry.Attempt} is scheduled after: {retry.Error}");
        }

        if (_claimed.Contains(issue.Id))
        {
            return ("claimed", "Claimed by a run that is finishing");
        }

        if (!Eligibility.HasRequiredFields(issue))
        {
            return ("invalid", "The tracker record has no id, identifier, title or state");
        }

        if (!config.IsActiveState(issue.State))
        {
            return ("invalid", $"State \"{issue.State}\" is also a terminal state");
        }

        if (!issue.Dispatchable)
        {
            var blocked = issue.HoldReason is not null && issue.HoldReason == HoldReasons.Blocked(issue.BlockedBy, config.IsTerminalState);
            return (blocked ? "blocked" : "held", issue.HoldReason ?? "The tracker adapter marked it not dispatchable");
        }

        var missing = Eligibility.MissingLabels(issue, config);
        if (missing.Count > 0)
        {
            return ("missing_labels", $"Missing required label {string.Join(", ", missing.Select(l => $"\"{l}\""))}");
        }

        if (BudgetBlock(issue.Id, config) is { } overBudget)
        {
            return ("paused", $"Paused: {overBudget}");
        }

        if (AvailableSlots(config) <= 0)
        {
            return ("waiting", $"All {config.Agent.MaxConcurrentAgents} agent slots are busy");
        }

        if (!StateSlotAvailable(issue.State, config))
        {
            return ("waiting", $"The agent limit for state \"{issue.State}\" is reached");
        }

        return ("ready", "Starts on the next poll");
    }

    private static RunningRow ToRow(RunningEntry e) => new(
        e.Issue.Id,
        e.Issue.Identifier,
        e.Issue.Url,
        e.Issue.State,
        e.SessionId,
        e.TurnCount,
        e.LastEvent,
        e.LastMessage,
        e.StartedAt,
        e.LastEventAt,
        new TokenCounts(e.InputTokens, e.OutputTokens, e.TotalTokens),
        e.WorkspacePath,
        e.Stop?.Reason);

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "cancelled",
        WorkerFailedException w => w.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
