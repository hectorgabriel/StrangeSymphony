using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Claude;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public class SessionResumeTests
{
    internal static void Session(OrchestratorHarness.WorkerControl w, string sessionId) =>
        w.Emit(new AgentEvent(AgentEventNames.SessionStarted, DateTimeOffset.UtcNow) { ThreadId = sessionId });

    internal static void Turn(OrchestratorHarness.WorkerControl w, long input, long output) =>
        w.Emit(new AgentEvent(AgentEventNames.TurnCompleted, DateTimeOffset.UtcNow) { TurnId = "1", AbsoluteTotals = new TokenTotals(input, output, input + output) });

    [Fact]
    public async Task A_retry_in_the_same_state_resumes_the_session_once()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Assert.Null(h.Workers[0].Resume);
        Session(h.Workers[0], "sess-1");
        Turn(h.Workers[0], 10, 5);
        h.Workers[0].Fail("kaboom");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry");

        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        Assert.Equal("sess-1", h.Workers[1].Resume);

        // The resumed run got nowhere, so the next one starts a new session.
        h.Workers[1].Fail("still broken");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "second retry");
        h.Time.Advance(TimeSpan.FromSeconds(20));
        await Wait.Until(() => h.Workers.Length == 3, "third dispatch");
        Assert.Null(h.Workers[2].Resume);
    }

    [Fact]
    public async Task A_run_in_another_state_starts_a_new_session()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Session(h.Workers[0], "sess-1");
        h.Tracker.Put(TestIssues.Make("1", state: "In Progress"));
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        Assert.Equal("In Progress", h.Workers[1].Issue.State);
        Assert.Null(h.Workers[1].Resume);
    }

    [Fact]
    public async Task Resume_can_be_turned_off()
    {
        await using var h = new OrchestratorHarness("claude:\n  resume: false");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Session(h.Workers[0], "sess-1");
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        Assert.Null(h.Workers[1].Resume);
    }

    [Fact]
    public async Task An_interrupted_run_resumes_its_session_after_a_restart()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Session(h.Workers[0], "sess-1");
        var path = OrchestratorStateFile.PathFor(h.Tmp.WorkspaceRoot);
        await Wait.Until(() => File.Exists(path) && File.ReadAllText(path).Contains("sess-1"), "session saved");

        await h.RestartAsync();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "restored retry");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "re-dispatch");
        Assert.Equal("sess-1", h.Workers[1].Resume);
    }

    [Fact]
    public void Resume_replaces_the_session_id_flag()
    {
        var settings = new ClaudeSettings("claude", null, new Dictionary<string, string>(), "acceptEdits", [], [], [], true, 1, 1, 1);
        var fresh = ClaudeCommandLine.BuildArgs(settings, "Todo", "sid", null, false, "WI-1: t").ToList();
        var resumed = ClaudeCommandLine.BuildArgs(settings, "Todo", "sid", null, false, "WI-1: t", resume: true).ToList();
        Assert.Equal("sid", fresh[fresh.IndexOf("--session-id") + 1]);
        Assert.DoesNotContain("--resume", fresh);
        Assert.Equal("sid", resumed[resumed.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--session-id", resumed);
    }
}

public sealed class WorkerResumeTests : IDisposable
{
    private sealed class Session(Func<string, Task> onTurn) : IAgentSession
    {
        public string? ThreadId => "thread";
        public Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct) => onTurn(prompt);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Factory(FakeTracker tracker, bool failResumedTurn = false) : IAgentSessionFactory
    {
        public List<string?> Resumes { get; } = [];
        public List<string> Prompts { get; } = [];

        public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
        {
            Resumes.Add(request.ResumeSessionId);
            return Task.FromResult<IAgentSession>(new Session(prompt =>
            {
                Prompts.Add(prompt);
                if (failResumedTurn && request.ResumeSessionId is not null)
                {
                    throw new AgentException(AgentErrorCodes.PortExit, "No conversation found with session ID");
                }

                tracker.Put(TestIssues.Make("1", state: "Done"));
                return Task.CompletedTask;
            }));
        }
    }

    private readonly TempWorkflow _tmp = new("tracker: {kind: fake}");
    private readonly FakeTracker _tracker = new();

    private EffectiveWorkflow Workflow()
    {
        _tmp.Write("tracker: {kind: fake}\nworkspace: {root: \"{ROOT}\"}", "Fix {{ issue.identifier }}");
        return _tmp.Store(_tracker).LoadInitial();
    }

    private Task Run(Factory factory, EffectiveWorkflow wf) =>
        new AgentWorker(new WorkspaceManager(NullLogger<WorkspaceManager>.Instance), factory, NullLogger<AgentWorker>.Instance)
            .RunAsync(TestIssues.Make("1"), 1, "sess-1", wf, _ => { }, _ => { }, default);

    [Fact]
    public async Task A_resumed_session_gets_the_resume_prompt_not_the_task_prompt()
    {
        var wf = Workflow();
        Directory.CreateDirectory(Path.Combine(_tmp.WorkspaceRoot, "WI-1"));
        _tracker.Put(TestIssues.Make("1"));
        var factory = new Factory(_tracker);

        await Run(factory, wf);

        Assert.Equal(["sess-1"], factory.Resumes);
        Assert.StartsWith("Symphony resumed this session for WI-1", Assert.Single(factory.Prompts));
    }

    [Fact]
    public async Task A_new_workspace_starts_a_new_session()
    {
        var wf = Workflow();
        _tracker.Put(TestIssues.Make("1"));
        var factory = new Factory(_tracker);

        await Run(factory, wf);

        Assert.Equal([null], factory.Resumes);
        Assert.Equal("Fix WI-1", Assert.Single(factory.Prompts));
    }

    [Fact]
    public async Task A_session_that_cannot_be_resumed_falls_back_to_a_new_one()
    {
        var wf = Workflow();
        Directory.CreateDirectory(Path.Combine(_tmp.WorkspaceRoot, "WI-1"));
        _tracker.Put(TestIssues.Make("1"));
        var factory = new Factory(_tracker, failResumedTurn: true);

        await Run(factory, wf);

        Assert.Equal(["sess-1", null], factory.Resumes);
        Assert.StartsWith("Symphony resumed", factory.Prompts[0]);
        Assert.Equal("Fix WI-1", factory.Prompts[1]);
    }

    public void Dispose() => _tmp.Dispose();
}

public class BudgetTests
{
    [Fact]
    public async Task A_task_over_its_budget_is_stopped_paused_and_commented()
    {
        await using var h = new OrchestratorHarness("budget:\n  max_tokens_per_issue: 100");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Turn(h.Workers[0], 60, 30);
        await Task.Delay(50);
        Assert.False(h.Workers[0].Token.IsCancellationRequested);

        SessionResumeTests.Turn(h.Workers[0], 100, 50);
        await Wait.Until(() => h.Workers[0].Token.IsCancellationRequested, "stop");
        await Wait.Until(() => h.Snap.Running.Count == 0, "exit");
        Assert.Empty(h.Snap.Retrying);
        await Wait.Until(() => { lock (h.Tracker.ToolCalls) return h.Tracker.ToolCalls.Count == 1; }, "comment");
        var (tool, args, issueId) = h.Tracker.ToolCalls[0];
        Assert.Equal(("add_comment", "1"), (tool, issueId));
        Assert.Contains("150 tokens", (string?)args["text"]);

        // Still active in the tracker, but not dispatched again; the board says why.
        await h.Tick();
        Assert.Single(h.Workers);
        var row = Assert.Single(h.Snap.Queue);
        Assert.Equal("paused", row.Status);
        Assert.Contains("task budget of 100", row.Reason);

        // The total survives a restart, because it comes from the metrics log.
        await h.RestartAsync();
        await h.Tick();
        Assert.Single(h.Workers);

        h.Tmp.Write(OrchestratorHarness.Yaml("budget:\n  max_tokens_per_issue: 1000"));
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "dispatch after raising the budget");
    }

    [Fact]
    public async Task The_daily_budget_stops_every_run_until_the_next_day()
    {
        await using var h = new OrchestratorHarness("agent:\n  max_concurrent_agents: 2\nbudget:\n  max_tokens_per_day: 100");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Tracker.Put(TestIssues.Make("2"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 2, "dispatch");
        SessionResumeTests.Session(h.Workers[0], "sess-1");
        SessionResumeTests.Turn(h.Workers[0], 100, 50);

        await Wait.Until(() => h.Snap.Retrying.Count == 2, "both waiting");
        Assert.All(h.Snap.Retrying, r => Assert.Contains("today's token budget of 100", r.Error));
        Assert.True(h.Snap.Budget!.DayExceeded);
        Assert.Equal(150, h.Snap.Budget.DayTokens);
        await Wait.Until(() => { lock (h.Tracker.ToolCalls) return h.Tracker.ToolCalls.Count == 2; }, "comments");

        // Not a backoff: nothing starts before the day ends, not even on a poll.
        h.Time.Advance(TimeSpan.FromHours(23));
        await h.Tick();
        Assert.Equal(2, h.Workers.Length);

        h.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 4, "next day");
        Assert.Equal(0, h.Snap.Budget!.DayTokens);
        // The stopped session is continued, not started over.
        Assert.Contains(h.Workers.Skip(2), w => w.Resume == "sess-1");
    }

    [Fact]
    public async Task Raising_the_daily_budget_lets_waiting_tasks_go_on()
    {
        await using var h = new OrchestratorHarness("budget:\n  max_tokens_per_day: 100");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Turn(h.Workers[0], 100, 50);
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "waiting");

        h.Tmp.Write(OrchestratorHarness.Yaml("budget:\n  max_tokens_per_day: 1000"));
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "dispatch after raising the budget");
    }

    [Fact]
    public void Budget_settings_parse_and_default_to_no_limit()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        ServiceConfig Build(string extra)
        {
            tmp.Write($"tracker: {{kind: fake}}\n{extra}");
            return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(tmp.Path), tmp.Path);
        }

        Assert.Equal(BudgetSettings.None, Build("").Budgets);
        Assert.True(Build("").Claude.Resume);
        Assert.Equal(new BudgetSettings(5000000, 40000000000), Build("budget:\n  max_tokens_per_issue: 5000000\n  max_tokens_per_day: \"40_000_000_000\"").Budgets);
        var ex = Assert.Throws<WorkflowException>(() => Build("budget:\n  max_tokens_per_day: -1"));
        Assert.Contains("budget.max_tokens_per_day", ex.Message);
    }
}

public class MetricsTests
{
    [Fact]
    public async Task Runs_handoffs_and_closes_add_up_and_survive_a_restart()
    {
        await using var h = new OrchestratorHarness("claude:\n  model: opus");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Turn(h.Workers[0], 100, 20);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Tracker.Put(TestIssues.Make("1", state: "Review"));
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Snap.Handoffs.Count == 1, "handoff");

        var first = h.Orchestrator.GetMetrics();
        Assert.Equal((1, 0, 0, 120L), (first.Totals.Runs, first.Totals.ReworkRuns, first.Totals.ClosedIssues, first.Totals.TotalTokens));

        // The reviewer sends it back: the same stage runs again, which is rework.
        h.Tracker.Put(TestIssues.Make("1"));
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        SessionResumeTests.Turn(h.Workers[1], 50, 10);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Tracker.Put(TestIssues.Make("1", state: "Done"));
        h.Workers[1].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Snap.Handoffs.Count == 1 && h.Snap.Handoffs[0].Closed, "closed");

        await h.RestartAsync();
        var m = h.Orchestrator.GetMetrics();
        Assert.Equal((2, 1, 1, 1, 180L), (m.Totals.Runs, m.Totals.Issues, m.Totals.ReworkRuns, m.Totals.ClosedIssues, m.Totals.TotalTokens));
        Assert.Equal(40, m.Totals.AgentSeconds);
        Assert.Equal(42, m.Totals.AvgCycleSeconds);
        var state = Assert.Single(m.ByState);
        Assert.Equal(("Todo", 2, 1, 1), (state.State, state.Runs, state.Issues, state.ReworkRuns));
        Assert.Equal(180, state.TokensByModel["opus"]);
        Assert.Equal("opus", Assert.Single(m.ByModel).Model);
        Assert.Equal((2, 1, 180L), (m.ByDay[0].Runs, m.ByDay[0].ClosedIssues, m.ByDay[0].TotalTokens));
        var issue = Assert.Single(m.Issues);
        Assert.Equal(("WI-1", 42d), (issue.Identifier, issue.CycleSeconds));

        var json = StatusServer.MetricsJson(m);
        Assert.Equal(2, (int)json["totals"]!["runs"]!);
        Assert.Equal(180, (long)json["by_state"]![0]!["tokens_by_model"]!["opus"]!);
    }

    [Fact]
    public async Task Failed_runs_are_counted_and_a_run_live_at_shutdown_is_recorded()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Workers[0].Fail("kaboom");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry");
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        SessionResumeTests.Turn(h.Workers[1], 7, 3);
        await Wait.Until(() => h.Snap.Totals.TotalTokens == 10, "tokens");

        await h.RestartAsync();
        var m = h.Orchestrator.GetMetrics();
        Assert.Equal((2, 1, 10L), (m.Totals.Runs, m.Totals.FailedRuns, m.Totals.TotalTokens));
        var lines = File.ReadAllLines(MetricsLog.PathFor(h.Tmp.WorkspaceRoot)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["failed", "interrupted"], lines.Select(l => (string?)l["outcome"]));
    }

    [Fact]
    public void A_damaged_line_does_not_lose_the_rest()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var path = MetricsLog.PathFor(tmp.Dir);
        var log = new MetricsLog(NullLogger.Instance);
        log.Add(path, new MetricRecord(MetricRecord.Run, "1", "WI-1", DateTimeOffset.UnixEpoch, "Todo", TotalTokens: 5));
        File.AppendAllText(path, "{ not json\n");
        log.Add(path, new MetricRecord(MetricRecord.Run, "1", "WI-1", DateTimeOffset.UnixEpoch, "Todo", TotalTokens: 7));

        var reloaded = new MetricsLog(NullLogger.Instance);
        reloaded.Load(path);
        Assert.Equal(12, reloaded.IssueTokens("1"));
    }
}

public class GateMetricsTests
{
    private static AgentEvent Gate(string state, bool passed, string? category = null, int seconds = 10) =>
        new(AgentEventNames.GateChecked, DateTimeOffset.UtcNow) { Message = "gate", Gate = new GateCheck(state, "Todo", passed, category, seconds) };

    [Fact]
    public async Task Gate_results_are_logged_per_status_and_survive_a_restart()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Workers[0].Emit(Gate("Review", false, "verification_failed", 30));
        h.Workers[0].Emit(Gate("Review", false, "not_pushed", 1));
        h.Workers[0].Emit(Gate("Review", true, seconds: 50));
        await Wait.Until(() => h.Orchestrator.GetMetrics().Totals.GateChecks == 3, "gate records");
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Orchestrator.GetMetrics().Totals.Runs == 1, "run record");

        await h.RestartAsync();
        var m = h.Orchestrator.GetMetrics();
        Assert.Equal((3, 2), (m.Totals.GateChecks, m.Totals.GateRejections));
        var gate = Assert.Single(m.ByGate!);
        Assert.Equal(("Review", 3, 1, 2, 1), (gate.State, gate.Checks, gate.Passed, gate.Rejected, gate.Issues));
        Assert.Equal((1, 0), (gate.Handoffs, gate.FirstTryHandoffs));
        Assert.Equal(27, gate.AvgSeconds);
        Assert.Equal(new Dictionary<string, int> { ["verification_failed"] = 1, ["not_pushed"] = 1 }, gate.RejectionsByCategory);
        var lines = File.ReadAllLines(MetricsLog.PathFor(h.Tmp.WorkspaceRoot)).Where(l => l.Contains("\"kind\":\"gate\"")).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains("\"from_state\":\"Todo\"", lines[0]);
        Assert.Contains("\"outcome\":\"passed\"", lines[2]);
        Assert.Equal(2, Assert.Single(m.Issues).GateRejections);
    }

    [Fact]
    public void A_hand_off_ends_at_a_pass_so_a_task_that_comes_back_starts_a_new_one()
    {
        var log = new MetricsLog(NullLogger.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"symphony-gate-metrics-{Guid.NewGuid():N}.jsonl");
        var t = DateTimeOffset.UtcNow;
        void Check(string issue, string state, string outcome, int minute) =>
            log.Add(path, new MetricRecord(MetricRecord.Gate, issue, $"WI-{issue}", t.AddMinutes(minute), state, Seconds: 10, Outcome: outcome));
        try
        {
            // WI-1: passes first time, comes back from review, fails once, then passes.
            Check("1", "Por Probar", "passed", 1);
            Check("1", "Por Probar", "verification_failed", 2);
            Check("1", "Por Probar", "passed", 3);
            // WI-2: passes first time. WI-3: still failing.
            Check("2", "por probar", "passed", 4);
            Check("3", "Por Probar", "not_pushed", 5);
            Check("3", "Por Probar", "not_pushed", 6);
            Check("1", "En Revision", "passed", 7);

            var report = log.Report(t, TimeZoneInfo.Utc, null);

            var probar = report.ByGate![0];
            Assert.Equal((6, 3, 3, 3), (probar.Checks, probar.Passed, probar.Rejected, probar.Issues));
            Assert.Equal((4, 2), (probar.Handoffs, probar.FirstTryHandoffs));
            Assert.Equal(2, probar.RejectionsByCategory["not_pushed"]);
            // Spellings of one status are one gate, shown with the latest spelling, busiest gate first.
            Assert.Equal(["Por Probar", "En Revision"], report.ByGate!.Select(g => g.State));
            var revision = report.ByGate![1];
            Assert.Equal((1, 1, 1), (revision.Checks, revision.Handoffs, revision.FirstTryHandoffs));
            // Gate records are not runs.
            Assert.Equal(0, report.Totals.Runs);

            // The names the /line page reads.
            var json = StatusServer.MetricsJson(report);
            Assert.Equal(7, json["totals"]!["gate_checks"]!.GetValue<int>());
            Assert.Equal(3, json["totals"]!["gate_rejections"]!.GetValue<int>());
            var gate = json["by_gate"]![0]!;
            Assert.Equal(2, gate["first_try_handoffs"]!.GetValue<int>());
            Assert.Equal(2, gate["rejections_by_category"]!["not_pushed"]!.GetValue<int>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
