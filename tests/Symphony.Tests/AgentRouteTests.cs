using Symphony.Core.Agents;
using Symphony.Core.Audit;
using Symphony.Core.Config;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

public class AgentRouteTests
{
    private static ServiceConfig Build(TempWorkflow tmp, string extra)
    {
        tmp.Write($"tracker: {{kind: fake}}\n{extra}");
        return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(tmp.Path), tmp.Path);
    }

    [Fact]
    public void The_runner_defaults_to_claude_and_routes_with_the_claude_models()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "claude:\n  model: sonnet\n  model_by_state: {Todo: opus}");

        Assert.Equal(AgentRunners.Claude, config.Agent.Runner);
        Assert.Same(config.Claude, config.RunnerSettings);
        Assert.Equal(new AgentRoute("claude", "opus"), config.RouteFor("todo"));
        Assert.Equal(new AgentRoute("claude", "sonnet"), config.RouteFor("In Progress"));
        Assert.Equal(AgentRunners.Claude, Build(tmp, "agent:\n  runner: \" Claude \"").Agent.Runner);
    }

    [Fact]
    public void An_unknown_runner_is_a_config_error()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");

        var ex = Assert.Throws<WorkflowException>(() => Build(tmp, "agent:\n  runner: codex"));

        Assert.Contains("agent.runner", ex.Message);
        Assert.Contains("claude, copilot", ex.Message);
    }

    [Fact]
    public void Stall_warnings_name_the_runners_timeout()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");

        var config = Build(tmp, "claude:\n  stall_timeout_ms: 60000\ngates:\n  Done:\n    run: [\"true\"]\n    timeout_ms: 60000");

        Assert.Contains(config.Warnings, w => w.Contains("reaches claude.stall_timeout_ms (60000)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_runner_means_claude()
    {
        Assert.Equal("claude", AgentRunners.Normalize(null));
        Assert.Equal("claude", AgentRunners.Normalize(" "));
        Assert.Equal("claude", AgentRunners.Normalize("CLAUDE"));
    }

    private sealed class StubFactory(string name) : IAgentSessionFactory
    {
        public List<AgentSessionRequest> Requests { get; } = [];

        public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromException<IAgentSession>(new InvalidOperationException(name));
        }
    }

    [Fact]
    public async Task The_router_starts_each_session_with_its_runners_factory()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "claude:\n  model: sonnet");
        var claude = new StubFactory("claude");
        var router = new AgentSessionRouter(new Dictionary<string, IAgentSessionFactory> { ["claude"] = claude });
        var request = new AgentSessionRequest(TestIssues.Make("1"), tmp.Dir, config, new FakeTracker());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => router.StartAsync(request, _ => { }, default));

        Assert.Equal("claude", ex.Message);
        // The factory gets the route the router chose, so both agree on the model.
        Assert.Equal(new AgentRoute("claude", "sonnet"), Assert.Single(claude.Requests).Route);
    }

    [Fact]
    public async Task A_route_to_a_runner_this_build_lacks_fails_as_agent_not_found()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "");
        var router = new AgentSessionRouter(new Dictionary<string, IAgentSessionFactory> { ["claude"] = new StubFactory("claude") });
        var request = new AgentSessionRequest(TestIssues.Make("1"), tmp.Dir, config, new FakeTracker(), Route: new AgentRoute("other", null));

        var ex = await Assert.ThrowsAsync<AgentException>(() => router.StartAsync(request, _ => { }, default));

        Assert.Equal(AgentErrorCodes.AgentNotFound, ex.Code);
        Assert.Contains("'other'", ex.Message);
    }

    [Fact]
    public async Task Runs_record_their_runner_in_the_state_file_metrics_and_audit_trail()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Session(h.Workers[0], "sess-1");
        var state = OrchestratorStateFile.PathFor(h.Tmp.WorkspaceRoot);
        await Wait.Until(() => File.Exists(state) && File.ReadAllText(state).Contains("\"resume_runner\": \"claude\"", StringComparison.Ordinal), "runner saved");

        h.Workers[0].Done.SetResult();
        var metrics = MetricsLog.PathFor(h.Tmp.WorkspaceRoot);
        await Wait.Until(() => File.Exists(metrics) && File.ReadAllText(metrics).Contains("\"runner\":\"claude\"", StringComparison.Ordinal), "run metric");
        var audit = File.ReadAllLines(AuditLog.DefaultPathFor(h.Tmp.WorkspaceRoot));
        Assert.Contains(audit, l => l.Contains("\"kind\":\"dispatched\"", StringComparison.Ordinal) && l.Contains("\"runner\":\"claude\"", StringComparison.Ordinal));
    }
}
