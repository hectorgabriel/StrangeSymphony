using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public sealed class OrchestratorHarness : IAsyncDisposable
{
    public sealed class WorkerControl
    {
        public required Issue Issue { get; init; }
        public required int? Attempt { get; init; }
        public required Action<AgentEvent> Emit { get; init; }
        public required CancellationToken Token { get; init; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed() => Done.TrySetResult();
        public void Fail(string why) => Done.TrySetException(new WorkerFailedException(why));
    }

    public TempWorkflow Tmp { get; }
    public FakeTracker Tracker { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    public WorkflowStore Store { get; }
    public Orchestrator Orchestrator { get; }
    public ConcurrentQueue<WorkerControl> Dispatches { get; } = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public OrchestratorHarness(string extraYaml = "")
    {
        Tmp = new TempWorkflow(Yaml(extraYaml));
        Store = Tmp.Store(Tracker);
        Store.LoadInitial();
        Orchestrator = new Orchestrator(Store, new WorkspaceManager(NullLogger<WorkspaceManager>.Instance), RunWorker, Time, NullLogger<Orchestrator>.Instance);
    }

    public static string Yaml(string extra) =>
        $"tracker:\n  kind: fake\npolling:\n  interval_ms: 600000\nworkspace:\n  root: \"{{ROOT}}\"\n{extra}";

    private async Task RunWorker(Issue issue, int? attempt, EffectiveWorkflow wf, Action<AgentEvent> onEvent, Action<string> onWorkspace, CancellationToken ct)
    {
        var control = new WorkerControl { Issue = issue, Attempt = attempt, Emit = onEvent, Token = ct };
        Dispatches.Enqueue(control);
        await control.Done.Task.WaitAsync(ct);
    }

    public void Start() => _run = Task.Run(() => Orchestrator.RunAsync(_stop.Token));

    public RuntimeSnapshot Snap => Orchestrator.GetSnapshot();

    public WorkerControl[] Workers => Dispatches.ToArray();

    public async Task Tick()
    {
        var before = Tracker.StateFetches;
        Orchestrator.RequestRefresh();
        await Wait.Until(() => Tracker.StateFetches > before || Store.LastError is not null || Tracker.FailIdFetches, "tick");
        await Task.Delay(50);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in Dispatches) w.Succeed();
        _stop.Cancel();
        if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(10));
        Store.Dispose();
        Tmp.Dispose();
    }
}

public class OrchestratorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2025-01-01T00:00:00Z");

    [Fact]
    public void Backoff_is_10s_exponential_and_capped()
    {
        Assert.Equal(10000, Orchestrator.BackoffMs(1, 300000));
        Assert.Equal(20000, Orchestrator.BackoffMs(2, 300000));
        Assert.Equal(80000, Orchestrator.BackoffMs(4, 300000));
        Assert.Equal(300000, Orchestrator.BackoffMs(10, 300000));
        Assert.Equal(15000, Orchestrator.BackoffMs(3, 15000));
        Assert.Equal(300000, Orchestrator.BackoffMs(500, 300000));
    }

    [Fact]
    public void Dispatch_sort_is_priority_then_oldest_then_identifier()
    {
        var issues = new[]
        {
            TestIssues.Make("1", "C", priority: null, createdAt: T0),
            TestIssues.Make("2", "B", priority: 2, createdAt: T0.AddDays(1)),
            TestIssues.Make("3", "A", priority: 2, createdAt: T0),
            TestIssues.Make("4", "Z", priority: 1, createdAt: null),
            TestIssues.Make("5", "Y", priority: 7, createdAt: T0.AddDays(-9)),
            TestIssues.Make("6", "X", priority: 2, createdAt: T0),
        };
        Assert.Equal(["Z", "A", "X", "B", "Y", "C"], Eligibility.SortForDispatch(issues).Select(i => i.Identifier));
    }

    [Fact]
    public async Task Dispatches_eligible_issues_within_global_limit()
    {
        await using var h = new OrchestratorHarness("agent:\n  max_concurrent_agents: 2");
        h.Tracker.Put(TestIssues.Make("1", priority: 3, createdAt: T0));
        h.Tracker.Put(TestIssues.Make("2", priority: 1, createdAt: T0.AddHours(1)));
        h.Tracker.Put(TestIssues.Make("3", priority: 1, createdAt: T0));
        h.Tracker.Put(TestIssues.Make("4", dispatchable: false, priority: 1));
        h.Tracker.Put(TestIssues.Make("5", state: "Done"));
        h.Start();

        await Wait.Until(() => h.Workers.Length == 2, "two dispatches");
        await Task.Delay(100);
        Assert.Equal(["2", "3"], h.Workers.Select(w => w.Issue.Id).Order());
        Assert.All(h.Workers, w => Assert.Null(w.Attempt));
        Assert.Equal(2, h.Snap.Running.Count);
    }

    [Fact]
    public async Task Required_labels_and_per_state_limits_apply()
    {
        await using var h = new OrchestratorHarness();
        h.Tmp.Write(OrchestratorHarness.Yaml("agent:\n  max_concurrent_agents_by_state:\n    todo: 1").Replace("kind: fake", "kind: fake\n  required_labels: [Agent]"));
        h.Store.ReloadIfChanged();
        h.Tracker.Put(TestIssues.Make("1", state: "Todo", labels: ["agent"]));
        h.Tracker.Put(TestIssues.Make("2", state: "todo", labels: ["AGENT", "x"]));
        h.Tracker.Put(TestIssues.Make("3", state: "In Progress", labels: ["agent"]));
        h.Tracker.Put(TestIssues.Make("4", state: "In Progress"));
        h.Start();

        await Wait.Until(() => h.Workers.Length == 2, "dispatches");
        await Task.Delay(100);
        var ids = h.Workers.Select(w => w.Issue.Id).Order().ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains("3", ids);
        Assert.Single(ids, id => id is "1" or "2");
    }

    [Fact]
    public async Task Normal_exit_schedules_one_second_continuation_retry()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation queued");
        var retry = h.Snap.Retrying[0];
        Assert.Equal(1, retry.Attempt);
        Assert.Null(retry.Error);
        Assert.Equal(h.Time.GetUtcNow().AddSeconds(1), retry.DueAt);

        h.Time.Advance(TimeSpan.FromMilliseconds(1000));
        await Wait.Until(() => h.Workers.Length == 2, "re-dispatch");
        Assert.Equal(1, h.Workers[1].Attempt);
    }

    [Fact]
    public async Task Failures_retry_with_exponential_backoff()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Fail("kaboom");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry queued");
        var retry = h.Snap.Retrying[0];
        Assert.Equal(1, retry.Attempt);
        Assert.Contains("kaboom", retry.Error);
        Assert.Equal(h.Time.GetUtcNow().AddSeconds(10), retry.DueAt);

        h.Time.Advance(TimeSpan.FromMilliseconds(9999));
        await Task.Delay(100);
        Assert.Single(h.Workers);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "retry dispatch");
        Assert.Equal(1, h.Workers[1].Attempt);

        h.Workers[1].Fail("again");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "second retry");
        Assert.Equal(2, h.Snap.Retrying[0].Attempt);
        Assert.Equal(h.Time.GetUtcNow().AddSeconds(20), h.Snap.Retrying[0].DueAt);
    }

    [Fact]
    public async Task Terminal_state_stops_worker_and_cleans_workspace()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        var ws = Path.Combine(h.Tmp.WorkspaceRoot, "WI-1");
        Directory.CreateDirectory(ws);
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Tracker.Put(TestIssues.Make("1", state: "Done"));
        await h.Tick();
        await Wait.Until(() => h.Workers[0].Token.IsCancellationRequested, "cancel");
        await Wait.Until(() => h.Snap.Running.Count == 0 && !Directory.Exists(ws), "released + cleaned");
        Assert.Empty(h.Snap.Retrying);
    }

    [Fact]
    public async Task Non_active_state_stops_worker_without_cleanup()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        var ws = Path.Combine(h.Tmp.WorkspaceRoot, "WI-1");
        Directory.CreateDirectory(ws);
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Tracker.Put(TestIssues.Make("1", state: "Human Review"));
        await h.Tick();
        await Wait.Until(() => h.Snap.Running.Count == 0, "released");
        Assert.True(Directory.Exists(ws));
        Assert.Empty(h.Snap.Retrying);
    }

    [Fact]
    public async Task Unroutable_or_missing_issue_stops_worker()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Tracker.Put(TestIssues.Make("2"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 2, "dispatch");

        h.Tracker.Put(TestIssues.Make("1", dispatchable: false));
        h.Tracker.Issues.TryRemove("2", out _);
        await h.Tick();
        await Wait.Until(() => h.Snap.Running.Count == 0, "both released");
    }

    [Fact]
    public async Task Active_refresh_updates_running_snapshot()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1", state: "Todo"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Tracker.Put(TestIssues.Make("1", state: "In Progress"));
        await h.Tick();
        await Wait.Until(() => h.Snap.Running.Single().State == "In Progress", "state updated");
    }

    [Fact]
    public async Task Reconcile_failure_keeps_workers_running()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Tracker.FailIdFetches = true;
        h.Tracker.Put(TestIssues.Make("1", state: "Done"));
        await h.Tick();
        Assert.False(h.Workers[0].Token.IsCancellationRequested);
        Assert.Single(h.Snap.Running);
    }

    [Fact]
    public async Task Stalled_session_is_killed_and_retried()
    {
        await using var h = new OrchestratorHarness("claude:\n  stall_timeout_ms: 1000");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Emit(new AgentEvent(AgentEventNames.Notification, h.Time.GetUtcNow()));
        h.Time.Advance(TimeSpan.FromMilliseconds(900));
        await h.Tick();
        Assert.False(h.Workers[0].Token.IsCancellationRequested);

        h.Time.Advance(TimeSpan.FromMilliseconds(200));
        await h.Tick();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "stall retry");
        Assert.True(h.Workers[0].Token.IsCancellationRequested);
        Assert.Contains("stalled", h.Snap.Retrying[0].Error);
    }

    [Fact]
    public async Task Retry_without_slots_requeues_with_reason()
    {
        await using var h = new OrchestratorHarness("agent:\n  max_concurrent_agents: 1");
        h.Tracker.Put(TestIssues.Make("1", priority: 1));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch 1");

        h.Workers[0].Fail("x");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry queued");
        h.Tracker.Put(TestIssues.Make("2", priority: 2));
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "issue 2 takes the slot");

        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => h.Snap.Retrying.SingleOrDefault()?.Error == "no available orchestrator slots", "requeued");
        Assert.Equal(2, h.Snap.Retrying[0].Attempt);
    }

    [Fact]
    public async Task Retry_releases_claim_when_issue_left_active_states()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation");
        h.Tracker.Put(TestIssues.Make("1", state: "Human Review"));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Snap.Retrying.Count == 0, "released");
        await Task.Delay(50);
        Assert.Single(h.Workers);
    }

    [Fact]
    public async Task Token_totals_use_deltas_of_absolute_reports()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        var now = h.Time.GetUtcNow();
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.SessionStarted, now) { ThreadId = "t", TurnId = "1" });
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.TurnCompleted, now) { AbsoluteTotals = new TokenTotals(100, 50, 150) });
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.TurnCompleted, now) { AbsoluteTotals = new TokenTotals(100, 50, 150) });
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.TurnCompleted, now) { AbsoluteTotals = new TokenTotals(300, 80, 380) });
        await Wait.Until(() => h.Snap.Totals.TotalTokens == 380, "totals");
        Assert.Equal(300, h.Snap.Totals.InputTokens);
        Assert.Equal(80, h.Snap.Totals.OutputTokens);
        Assert.Equal("t-1", h.Snap.Running[0].SessionId);
        Assert.Equal(380, h.Snap.Running[0].Tokens.TotalTokens);
    }

    [Fact]
    public async Task Candidate_fetch_failure_skips_dispatch_and_invalid_workflow_blocks_it()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.FailStateFetches = true;
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Tracker.StateFetches >= 1, "first tick");
        await Task.Delay(100);
        Assert.Empty(h.Workers);

        h.Tracker.FailStateFetches = false;
        File.WriteAllText(h.Tmp.Path, "---\ntracker: [broken\n---\nx");
        h.Orchestrator.RequestRefresh();
        await Wait.Until(() => h.Snap.WorkflowError is not null, "workflow error surfaced");
        Assert.Empty(h.Workers);

        h.Tmp.Write(OrchestratorHarness.Yaml(""));
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch after fix");
    }

    [Fact]
    public async Task Snapshot_reports_runtime_including_active_sessions()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30, h.Snap.Totals.SecondsRunning, precision: 0);
        Assert.Equal("running", h.Snap.Issues["WI-1"].Status);
    }
}
