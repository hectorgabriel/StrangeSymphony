using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Claude;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>Real Integration Profile (SPEC 17.8). Reported as skipped unless SYMPHONY_REAL_CLAUDE=1.</summary>
public sealed class RealClaudeFactAttribute : FactAttribute
{
    public RealClaudeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SYMPHONY_REAL_CLAUDE") != "1")
        {
            Skip = "set SYMPHONY_REAL_CLAUDE=1 to run against the installed claude CLI (uses your Claude login)";
        }
    }
}

public class RealIntegrationTests
{
    [RealClaudeFact]
    public async Task Real_claude_runs_two_turns_and_calls_host_side_tracker_tool()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        tmp.Write("tracker: {kind: fake}\nworkspace: {root: \"{ROOT}\"}\nclaude:\n  model: haiku\n  turn_timeout_ms: 120000");
        var tracker = new FakeTracker();
        var config = new ConfigBuilder([new FakeTrackerFactory(tracker)], _ => null).Build(WorkflowLoader.Load(tmp.Path), tmp.Path);
        var ws = Path.Combine(config.WorkspaceRoot, "WI-1");
        Directory.CreateDirectory(ws);

        await using var gateway = new McpToolGateway(NullLogger<McpToolGateway>.Instance);
        await gateway.StartAsync(NullLoggerFactory.Instance, default);
        var events = new List<AgentEvent>();
        var factory = new ClaudeCodeSessionFactory(gateway, Path.Combine(tmp.Dir, "state"), NullLoggerFactory.Instance);

        await using (var session = await factory.StartAsync(new AgentSessionRequest(TestIssues.Make("1"), ws, config, tracker), e => { lock (events) events.Add(e); }, default))
        {
            await session.RunTurnAsync("Call the tracker echo tool exactly once with arguments {\"ping\": 1}, then reply with just: ok", TestIssues.Make("1"), default);
            await session.RunTurnAsync("Reply with just: second", TestIssues.Make("1"), default);
        }

        lock (events)
        {
            Assert.Equal(2, events.Count(e => e.Event == AgentEventNames.TurnCompleted));
            Assert.True(events.Last(e => e.Event == AgentEventNames.TurnCompleted).AbsoluteTotals!.TotalTokens > 0);
        }

        lock (tracker.ToolCalls)
        {
            var call = Assert.Single(tracker.ToolCalls);
            Assert.Equal("echo", call.Tool);
            Assert.Equal("1", call.IssueId);
        }
    }
}
