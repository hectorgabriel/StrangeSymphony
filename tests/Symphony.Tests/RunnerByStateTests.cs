using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public class RunnerByStateTests
{
    private static ServiceConfig Build(TempWorkflow tmp, string extra)
    {
        tmp.Write($"tracker: {{kind: fake}}\n{extra}");
        return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(tmp.Path), tmp.Path);
    }

    [Fact]
    public void A_state_can_run_on_another_runner_with_that_runners_models()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent:\n  runner_by_state: {\" in progress \": Copilot}\nclaude:\n  model: opus\ncopilot:\n  model: gpt-6-sol\n  model_by_state: {Todo: never-used}");

        Assert.Equal(AgentRunners.Claude, config.Agent.Runner);
        Assert.Equal(new AgentRoute("claude", "opus"), config.RouteFor("Todo"));
        Assert.Equal(new AgentRoute("copilot", "gpt-6-sol"), config.RouteFor("In Progress"));
        Assert.Equal(["claude", "copilot"], config.Agent.RunnersInUse);
        Assert.Same(config.Claude, config.RunnerSettings);
    }

    [Fact]
    public void The_default_runner_can_be_copilot_with_claude_for_one_state()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent:\n  runner: copilot\n  runner_by_state: {Todo: claude}");

        Assert.Equal(new AgentRoute("claude", null), config.RouteFor("Todo"));
        Assert.Equal(new AgentRoute("copilot", null), config.RouteFor("In Progress"));
    }

    [Theory]
    [InlineData("agent:\n  runner_by_state: {Todo: codex}", "agent.runner_by_state.Todo")]
    [InlineData("agent:\n  runner_by_state: {Todo: \"\"}", "agent.runner_by_state.Todo")]
    [InlineData("agent:\n  runner_by_state: [copilot]", "agent.runner_by_state")]
    public void Bad_entries_are_config_errors(string yaml, string field)
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var ex = Assert.Throws<WorkflowException>(() => Build(tmp, yaml));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void A_state_that_is_not_active_gets_a_warning()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent:\n  runner_by_state: {Done: copilot}");
        Assert.Contains(config.Warnings, w => w.StartsWith("agent.runner_by_state names 'Done'", StringComparison.Ordinal));
    }

    [Fact]
    public void Gate_warnings_use_the_tightest_stall_timeout_of_the_runners_in_use()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent:\n  runner_by_state: {Todo: copilot}\ncopilot:\n  stall_timeout_ms: 60000\ngates:\n  Done:\n    run: [\"true\"]\n    timeout_ms: 70000");

        Assert.Contains(config.Warnings, w => w.Contains("reaches copilot.stall_timeout_ms (60000)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_worker_ends_the_session_when_the_issue_moves_to_a_state_on_another_runner()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var tracker = new FakeTracker();
        tmp.Write("tracker: {kind: fake}\nworkspace: {root: \"{ROOT}\"}\nagent:\n  runner_by_state: {In Progress: copilot}");
        var wf = tmp.Store(tracker).LoadInitial();
        tracker.Put(TestIssues.Make("1"));
        var routes = new List<AgentRoute>();
        var factory = new RouteRecordingFactory(routes, () => tracker.Put(TestIssues.Make("1", state: "In Progress")));

        await new AgentWorker(new WorkspaceManager(NullLogger<WorkspaceManager>.Instance), factory, NullLogger<AgentWorker>.Instance)
            .RunAsync(TestIssues.Make("1"), null, wf, _ => { }, _ => { }, default);

        Assert.Equal([new AgentRoute("claude", null)], routes);
        Assert.Equal(1, factory.Turns);
    }

    private sealed class RouteRecordingFactory(List<AgentRoute> routes, Action onTurn) : IAgentSessionFactory
    {
        public int Turns { get; private set; }

        public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
        {
            routes.Add(request.EffectiveRoute);
            return Task.FromResult<IAgentSession>(new Session(this, onTurn));
        }

        private sealed class Session(RouteRecordingFactory owner, Action onTurn) : IAgentSession
        {
            public string? ThreadId => "t";

            public Task RunTurnAsync(string prompt, Core.Domain.Issue currentIssue, CancellationToken ct)
            {
                owner.Turns++;
                onTurn();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Each_session_is_checked_against_its_own_runners_stall_timeout()
    {
        // Claude sessions stall after 1 s; Copilot's detection is off.
        await using var h = new OrchestratorHarness("agent:\n  max_concurrent_agents: 2\n  runner_by_state: {In Progress: copilot}\nclaude:\n  stall_timeout_ms: 1000\ncopilot:\n  stall_timeout_ms: 0");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Tracker.Put(TestIssues.Make("2", state: "In Progress"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 2, "dispatch");
        Assert.Equal(["claude", "copilot"], h.Snap.Running.OrderBy(r => r.IssueId).Select(r => r.Runner));

        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.Tick();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "claude session stalled");
        Assert.Equal("1", h.Snap.Retrying[0].IssueId);
        Assert.Equal("2", Assert.Single(h.Snap.Running).IssueId);
    }

    [Fact]
    public async Task A_session_is_not_resumed_on_another_runner()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Session(h.Workers[0], "sess-1");
        h.Workers[0].Fail("kaboom");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry");

        // Same state, but the workflow now sends it to Copilot: a Claude session id means nothing there.
        h.Tmp.Write(OrchestratorHarness.Yaml("agent:\n  runner_by_state: {Todo: copilot}"));
        await h.Tick();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => h.Workers.Length == 2, "second dispatch");
        Assert.Null(h.Workers[1].Resume);
        Assert.Equal("copilot", Assert.Single(h.Snap.Running).Runner);
    }

    [Fact]
    public async Task Copilot_ai_credits_are_recorded_per_run_and_reported()
    {
        await using var h = new OrchestratorHarness("agent: {runner: copilot}\ncopilot: {model: gpt-6-sol}");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.TurnCompleted, DateTimeOffset.UtcNow) { TurnId = "1", AbsoluteTotals = new TokenTotals(100, 10, 110, 2_500_000_000) });
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.TurnCompleted, DateTimeOffset.UtcNow) { TurnId = "2", AbsoluteTotals = new TokenTotals(200, 20, 220, 4_000_000_000) });
        h.Tracker.Put(TestIssues.Make("1", state: "Review"));
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Orchestrator.GetMetrics().Totals.Runs == 1, "run recorded");

        var m = h.Orchestrator.GetMetrics();
        Assert.Equal(4, m.Totals.AiCredits);
        var model = Assert.Single(m.ByModel);
        Assert.Equal(("copilot", "gpt-6-sol", 4d), (model.Runner, model.Model, model.AiCredits));
        Assert.Contains("\"nano_aiu\":4000000000", File.ReadAllText(MetricsLog.PathFor(h.Tmp.WorkspaceRoot)), StringComparison.Ordinal);
        Assert.Equal(4, (double)Symphony.Cli.StatusServer.MetricsJson(m)["totals"]!["ai_credits"]!);
    }

    [Fact]
    public void Metrics_group_models_by_runner()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var path = Path.Combine(tmp.Dir, "metrics.jsonl");
        var log = new MetricsLog(NullLogger.Instance);
        var at = DateTimeOffset.UnixEpoch;
        log.Add(path, new MetricRecord(MetricRecord.Run, "1", "WI-1", at, "Todo", "claude-sonnet-5.5", TotalTokens: 10));
        log.Add(path, new MetricRecord(MetricRecord.Run, "1", "WI-1", at, "Todo", "claude-sonnet-5.5", TotalTokens: 20, Runner: "claude"));
        log.Add(path, new MetricRecord(MetricRecord.Run, "2", "WI-2", at, "Todo", "claude-sonnet-5.5", TotalTokens: 40, Runner: "copilot"));

        var byModel = log.Report(at, TimeZoneInfo.Utc, null).ByModel;

        Assert.Equal([("copilot", "claude-sonnet-5.5", 1, 40L), ("claude", "claude-sonnet-5.5", 2, 30L)],
            byModel.Select(m => (m.Runner, m.Model, m.Runs, m.TotalTokens)));
    }
}
