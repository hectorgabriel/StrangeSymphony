using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Core.Orchestration;

/// <summary>
/// Single-authority scheduler (SPEC 7, 8, 16). All state mutations happen on one message loop;
/// workers, timers and the HTTP surface only post messages.
/// </summary>
public sealed class Orchestrator
{
    public delegate Task WorkerRunner(
        Issue issue,
        int? attempt,
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

    private enum StopKind { Release, Stalled, Shutdown }

    private sealed record StopDisposition(StopKind Kind, bool CleanupWorkspace, string Reason);

    private sealed class RunningEntry
    {
        public required Guid RunId { get; init; }
        public required Issue Issue { get; set; }
        public required int? RetryAttempt { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required CancellationTokenSource Cts { get; init; }
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
    }

    private sealed class IssueTrack
    {
        public required string IssueId { get; init; }
        public required string Identifier { get; set; }
        public string? WorkspacePath { get; set; }
        public int RestartCount { get; set; }
        public string? LastError { get; set; }
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
        IReadOnlyDictionary<string, IssueDetail> Issues);

    private readonly WorkflowStore _store;
    private readonly WorkspaceManager _workspaces;
    private readonly WorkerRunner _runWorker;
    private readonly TimeProvider _time;
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<Msg> _inbox = Channel.CreateUnbounded<Msg>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, RunningEntry> _running = new();
    private readonly HashSet<string> _claimed = new();
    private readonly Dictionary<string, RetryEntry> _retries = new();
    private readonly HashSet<string> _completed = new();
    private readonly Dictionary<string, IssueTrack> _tracks = new();
    private readonly List<Task> _cleanups = new();
    private long _inputTokens;
    private long _outputTokens;
    private long _totalTokens;
    private double _endedSeconds;
    private JsonNode? _rateLimits;
    private ITimer? _tickTimer;
    private int _tickQueued;
    private volatile PublishedState? _published;

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
        _store.Reloaded += _ => RequestRefresh();
    }

    /// <summary>Bookkeeping only; not used for dispatch gating.</summary>
    public int CompletedCount => _completed.Count;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await StartupTerminalCleanupAsync(stoppingToken);
        _tickTimer = _time.CreateTimer(_ => RequestRefresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        RequestRefresh();
        Publish();

        try
        {
            await foreach (var msg in _inbox.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleAsync(msg, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
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
            p.Issues);
    }

    private Task HandleAsync(Msg msg, CancellationToken ct) => msg switch
    {
        TickMsg => OnTickAsync(ct),
        WorkerExitedMsg m => Sync(() => OnWorkerExited(m)),
        AgentUpdateMsg m => Sync(() => OnAgentUpdate(m)),
        WorkspaceMsg m => Sync(() => OnWorkspace(m)),
        RetryDueMsg m => OnRetryDueAsync(m, ct),
        _ => Task.CompletedTask,
    };

    private static Task Sync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private void Post(Msg msg) => _inbox.Writer.TryWrite(msg);

    // ---- Tick (SPEC 8.1, 16.2) -------------------------------------------------------------

    private async Task OnTickAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _tickQueued, 0);
        _store.ReloadIfChanged();
        var workflow = _store.Current;
        var config = workflow.Config;

        try
        {
            await ReconcileAsync(workflow, ct);

            if (_store.LastError is { } error)
            {
                _logger.LogError("action=dispatch outcome=skipped reason=invalid_workflow code={Code} error={Error}", error.Code, error.Message);
                return;
            }

            IReadOnlyList<Issue> issues;
            try
            {
                issues = await workflow.Tracker.FetchIssuesByStatesAsync(config.Tracker.ActiveStates, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("action=fetch_candidates outcome=failed reason={Reason}", ex.Message);
                return;
            }

            foreach (var issue in Eligibility.SortForDispatch(issues))
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
            _tickTimer?.Change(TimeSpan.FromMilliseconds(_store.Current.Config.PollIntervalMs), Timeout.InfiniteTimeSpan);
        }
    }

    // ---- Reconciliation (SPEC 8.5, 16.3) -----------------------------------------------------

    private async Task ReconcileAsync(EffectiveWorkflow workflow, CancellationToken ct)
    {
        var config = workflow.Config;
        ReconcileStalled(config);

        var ids = _running.Values.Where(e => e.Stop is null).Select(e => e.Issue.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        IReadOnlyList<Issue> refreshed;
        try
        {
            refreshed = await workflow.Tracker.FetchIssuesByIdsAsync(ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("action=reconcile outcome=failed reason={Reason} note=keeping_workers", ex.Message);
            return;
        }

        var returned = new HashSet<string>();
        foreach (var issue in refreshed)
        {
            returned.Add(issue.Id);
            if (!_running.TryGetValue(issue.Id, out var entry) || entry.Stop is not null)
            {
                continue;
            }

            if (config.IsTerminalState(issue.State))
            {
                entry.Issue = issue;
                Terminate(entry, cleanupWorkspace: true, $"terminal_state:{issue.State}");
            }
            else if (config.IsActiveState(issue.State) && Eligibility.IsRoutable(issue, config))
            {
                entry.Issue = issue;
            }
            else
            {
                entry.Issue = issue;
                Terminate(entry, cleanupWorkspace: false, $"not_active_or_unroutable:{issue.State}");
            }
        }

        foreach (var missing in ids.Where(id => !returned.Contains(id)))
        {
            if (_running.TryGetValue(missing, out var entry) && entry.Stop is null)
            {
                Terminate(entry, cleanupWorkspace: false, "not_visible");
            }
        }
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
        && HasSlotFor(issue, config);

    private void Dispatch(Issue issue, EffectiveWorkflow workflow, int? attempt)
    {
        var entry = new RunningEntry
        {
            RunId = Guid.NewGuid(),
            Issue = issue,
            RetryAttempt = attempt,
            StartedAt = _time.GetUtcNow(),
            Cts = new CancellationTokenSource(),
        };

        _running[issue.Id] = entry;
        _claimed.Add(issue.Id);
        CancelRetry(issue.Id);

        var track = Track(issue);
        if (attempt is not null)
        {
            track.RestartCount++;
        }

        _logger.LogInformation("action=dispatch outcome=started issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} attempt={Attempt}",
            issue.Id, issue.Identifier, issue.State, attempt);

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
        if (ev.RateLimits is not null) _rateLimits = ev.RateLimits.DeepClone();

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
        _endedSeconds += Math.Max(0, (_time.GetUtcNow() - entry.StartedAt).TotalSeconds);
        var issue = entry.Issue;
        var nextAttempt = (entry.RetryAttempt ?? 0) + 1;

        switch (entry.Stop?.Kind)
        {
            case StopKind.Release:
                _logger.LogInformation("action=worker_exit outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason}",
                    issue.Id, issue.Identifier, entry.SessionId, entry.Stop.Reason);
                Release(issue.Id);
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
            ScheduleRetry(issue.Id, 1, issue.Identifier, issue.Url, error: null, continuation: true);
        }
        else
        {
            var reason = $"worker exited: {Describe(m.Error)}";
            SetLastError(issue.Id, reason);
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

    private void ScheduleRetry(string issueId, int attempt, string identifier, string? url, string? error, bool continuation)
    {
        CancelRetry(issueId);
        var delayMs = continuation ? ContinuationDelayMs : BackoffMs(attempt, _store.Current.Config.Agent.MaxRetryBackoffMs);
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
        };
        _claimed.Add(issueId);
        _logger.LogInformation("action=retry outcome=retrying issue_id={IssueId} issue_identifier={IssueIdentifier} attempt={Attempt} delay_ms={DelayMs} kind={Kind} error={Error}",
            issueId, identifier, attempt, delayMs, continuation ? "continuation" : "failure", error);
    }

    private void CancelRetry(string issueId)
    {
        if (_retries.Remove(issueId, out var existing))
        {
            existing.Timer.Dispose();
        }
    }

    private async Task OnRetryDueAsync(RetryDueMsg m, CancellationToken ct)
    {
        if (!_retries.TryGetValue(m.IssueId, out var retry) || retry.Token != m.Token)
        {
            return;
        }

        _retries.Remove(m.IssueId);
        retry.Timer.Dispose();
        var workflow = _store.Current;
        var config = workflow.Config;

        IReadOnlyList<Issue> refreshed;
        try
        {
            refreshed = await workflow.Tracker.FetchIssuesByIdsAsync([m.IssueId], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ScheduleRetry(m.IssueId, retry.Attempt + 1, retry.Identifier, retry.Url, $"retry refresh failed: {ex.Message}", continuation: false);
            return;
        }

        var issue = refreshed.FirstOrDefault(i => i.Id == m.IssueId);
        if (issue is null)
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=not_visible", m.IssueId, retry.Identifier);
            Release(m.IssueId);
            return;
        }

        if (config.IsTerminalState(issue.State))
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=terminal_state state={State}", issue.Id, issue.Identifier, issue.State);
            Release(issue.Id);
            StartCleanup(issue.Identifier);
            return;
        }

        if (!Eligibility.HasRequiredFields(issue) || !config.IsActiveState(issue.State) || !Eligibility.IsRoutable(issue, config))
        {
            _logger.LogInformation("action=retry outcome=released issue_id={IssueId} issue_identifier={IssueIdentifier} reason=not_active_or_unroutable state={State}", issue.Id, issue.Identifier, issue.State);
            Release(issue.Id);
            return;
        }

        if (_store.LastError is not null)
        {
            ScheduleRetry(issue.Id, retry.Attempt + 1, issue.Identifier, issue.Url, "workflow invalid; dispatch blocked", continuation: false);
            return;
        }

        if (!HasSlotFor(issue, config))
        {
            ScheduleRetry(issue.Id, retry.Attempt + 1, issue.Identifier, issue.Url, "no available orchestrator slots", continuation: false);
            return;
        }

        Dispatch(issue, workflow, retry.Attempt);
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
        var workflow = _store.Current;
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
        _tickTimer?.Dispose();
        foreach (var retry in _retries.Values)
        {
            retry.Timer.Dispose();
        }

        var workers = new List<Task>();
        foreach (var entry in _running.Values)
        {
            entry.Stop ??= new StopDisposition(StopKind.Shutdown, false, "shutdown");
            entry.Cts.Cancel();
            if (entry.Worker is not null) workers.Add(entry.Worker);
        }

        var grace = TimeSpan.FromMilliseconds(_store.Current.Config.Hooks.TimeoutMs + 15000);
        try
        {
            await Task.WhenAll(workers.Concat(_cleanups)).WaitAsync(grace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("action=shutdown outcome=incomplete reason={Reason}", ex.Message);
        }

        _logger.LogInformation("action=shutdown outcome=completed stopped_workers={Count}", workers.Count);
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
            issues);
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
