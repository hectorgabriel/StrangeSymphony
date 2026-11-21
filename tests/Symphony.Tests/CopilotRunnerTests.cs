using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;
using Symphony.Copilot;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>
/// Drives CopilotSession against a fake `copilot` that prints the JSON lines Copilot CLI 1.0.114 printed in a real
/// run (docs/copilot.md). Like the fake claude, it is a bash script, which goes through Git Bash on Windows.
/// </summary>
public sealed class CopilotRunnerTests : IDisposable
{
    private const string FakeCopilot = """
        #!/usr/bin/env bash
        OUT="$(dirname "$0")"
        { printf '%s\n' "$@"; echo '---'; } >> "$OUT/args.txt"
        { pwd -W 2>/dev/null || pwd; } > "$OUT/cwd.txt"
        env > "$OUT/env.txt"
        SID=""; RESUME=""; USAGE=""; MCP=""
        for a in "$@"; do
          case "$a" in
            --session-id=*) SID="${a#--session-id=}" ;;
            --resume=*) RESUME="${a#--resume=}"; SID="$RESUME" ;;
            --usage-output-file=*) USAGE="${a#--usage-output-file=}" ;;
            --additional-mcp-config=@*) MCP="${a#--additional-mcp-config=@}" ;;
          esac
        done
        PROMPT="$(cat)"
        { printf '%s\n' "$PROMPT"; echo '---'; } >> "$OUT/stdin.txt"
        if [ "$RESUME" = "gone-session" ]; then echo "Error: No session, task, or name matched '$RESUME'." >&2; exit 1; fi
        COUNT_FILE="$OUT/count-$SID"; n=$(( $(cat "$COUNT_FILE" 2>/dev/null || echo 0) + 1 )); echo $n > "$COUNT_FILE"
        if [ -n "$RESUME" ]; then START=session.resume; else START=session.start; fi
        echo '{"type":"'$START'","data":{"sessionId":"'"$SID"'","copilotVersion":"1.0.114"},"id":"1","timestamp":"2026-11-20T22:51:25.200Z","parentId":null}'
        if [ -n "$MCP" ]; then
          STATUS=connected; case "$PROMPT" in *MCP_NEEDS_AUTH*) STATUS=needs-auth ;; esac
          echo '{"type":"session.mcp_servers_loaded","data":{"servers":[{"name":"github-mcp-server","status":"disabled","source":"builtin"},{"name":"tracker","status":"'$STATUS'"}]},"ephemeral":true,"id":"2"}'
        fi
        echo 'this is not json'
        case "$PROMPT" in
          *EXIT_NOW*) echo "fatal: boom" >&2; exit 3 ;;
          *GO_SILENT*) sleep 30 ;;
          *SESSION_ERROR*) echo '{"type":"session.error","data":{"errorType":"quota","message":"monthly quota reached","statusCode":402},"id":"9"}'; echo '{"type":"result","sessionId":"'"$SID"'","exitCode":1}'; exit 1 ;;
        esac
        echo '{"type":"assistant.tool_call_delta","data":{"toolCallId":"t1","toolName":"bash","inputDelta":"{"},"ephemeral":true,"id":"3"}'
        echo '{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"bash","arguments":{"command":"touch x"}},"id":"4"}'
        echo '{"type":"tool.execution_complete","data":{"toolCallId":"t1","success":false,"error":{"message":"Permission denied because no interactive user response was available."}},"id":"5"}'
        echo '{"type":"assistant.message","data":{"content":"working '$n'","toolRequests":[]},"id":"6"}'
        echo '{"type":"session.warning","data":{"warningType":"hook","message":"hook said hi"},"id":"7"}'
        cat > "$USAGE" <<EOF
        {"totalNanoAiu":$((1500000000*n)),"tokenDetails":{"input":{"tokenCount":$((10*n))},"cache_read":{"tokenCount":$((5*n))},"cache_write":{"tokenCount":$((5*n))},"output":{"tokenCount":$((7*n))}}}
        EOF
        echo '{"type":"result","timestamp":"2026-11-20T22:51:30.063Z","sessionId":"'"$SID"'","exitCode":0,"usage":{"premiumRequests":'$n'}}'
        """;

    private readonly TempWorkflow _tmp;
    private readonly string _bin;
    private readonly List<AgentEvent> _events = [];
    private readonly FakeTracker _tracker = new();

    public CopilotRunnerTests()
    {
        _tmp = new TempWorkflow("tracker: {kind: fake}");
        _bin = Path.Combine(_tmp.Dir, "bin");
        Directory.CreateDirectory(_bin);
        var script = Path.Combine(_bin, "fake-copilot");
        File.WriteAllText(script, FakeCopilot.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string StateDir => Path.Combine(_tmp.Dir, "state");

    private (ServiceConfig Config, string Workspace) Setup(string copilotYaml = "")
    {
        _tmp.Write($"tracker: {{kind: fake}}\nworkspace: {{root: \"{{ROOT}}\"}}\nagent: {{runner: copilot}}\ncopilot:\n  command: \"{TempWorkflow.YamlPath(Path.Combine(_bin, "fake-copilot"))}\"\n  model: claude-sonnet-5.5\n  allow_tools: [write, \"shell(git:*)\"]\n  deny_tools: [\"shell(git push)\"]\n{copilotYaml}");
        var config = new ConfigBuilder([new FakeTrackerFactory(_tracker)], _ => null).Build(WorkflowLoader.Load(_tmp.Path), _tmp.Path);
        var ws = Path.Combine(config.WorkspaceRoot, "WI-1");
        Directory.CreateDirectory(ws);
        return (config, ws);
    }

    private async Task<IAgentSession> Start(ServiceConfig config, string ws, IToolGateway? gateway = null, string? resume = null) =>
        await new CopilotSessionFactory(gateway, StateDir, NullLoggerFactory.Instance)
            .StartAsync(new AgentSessionRequest(TestIssues.Make("1"), ws, config, _tracker, resume), e => { lock (_events) _events.Add(e); }, default);

    private string Read(string file) => File.ReadAllText(Path.Combine(_bin, file));

    /// <summary>The arguments of each run, in order.</summary>
    private List<string[]> Runs() =>
        Read("args.txt").Split("---\n", StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();

    private List<AgentEvent> Events()
    {
        lock (_events) return _events.ToList();
    }

    [Fact]
    public async Task Each_turn_is_one_run_that_creates_then_resumes_the_session()
    {
        var (config, ws) = Setup();
        string? id;
        await using (var session = await Start(config, ws))
        {
            await session.RunTurnAsync("first prompt", TestIssues.Make("1"), default);
            await session.RunTurnAsync("continue please", TestIssues.Make("1"), default);
            id = session.ThreadId;
        }

        Assert.True(Guid.TryParse(id, out _));
        var runs = Runs();
        Assert.Equal(2, runs.Count);
        Assert.Contains($"--session-id={id}", runs[0]);
        Assert.Contains($"--resume={id}", runs[1]);
        Assert.DoesNotContain(runs[1], a => a.StartsWith("--session-id", StringComparison.Ordinal));
        Assert.Equal(["first prompt", "continue please"], Read("stdin.txt").Split("---\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('\n')));

        var events = Events();
        Assert.Equal(AgentEventNames.SessionStarted, events[0].Event);
        var completed = events.Where(e => e.Event == AgentEventNames.TurnCompleted).ToList();
        Assert.Equal(["1", "2"], completed.Select(e => e.TurnId));
        // The usage file counts the whole session: input includes cache reads and writes.
        Assert.Equal(new TokenTotals(20, 7, 27, 1_500_000_000), completed[0].AbsoluteTotals);
        Assert.Equal(new TokenTotals(40, 14, 54, 3_000_000_000), completed[1].AbsoluteTotals);
        Assert.Equal("working 1 [permission denials: 1]", completed[0].Message);
        Assert.Contains(events, e => e.Event == AgentEventNames.ToolCall && e.Message == "tool:bash");
        Assert.Contains(events, e => e.Event == AgentEventNames.Notification && e.Message!.StartsWith("bash failed: Permission denied", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Event == AgentEventNames.Notification && e.Message == "warning: hook said hi");
        Assert.Contains(events, e => e.Event == AgentEventNames.Malformed);
        Assert.DoesNotContain(events, e => e.Message == "assistant.tool_call_delta");
        // Per-run usage files are deleted; only the session's baseline stays.
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(StateDir, "copilot-usage")), f => !Path.GetFileName(f).StartsWith("session-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Launches_in_the_workspace_with_unattended_flags_and_a_private_home()
    {
        Environment.SetEnvironmentVariable("FAKE_TRACKER_TOKEN", "should-not-leak");
        Environment.SetEnvironmentVariable("GH_TOKEN", "ghp_should_not_leak");
        Environment.SetEnvironmentVariable("COPILOT_ALLOW_ALL", "true");
        try
        {
            var (config, ws) = Setup("  reasoning_effort: high\n  excluded_tools: [web_fetch]\n  max_ai_credits: 50");
            await using (var session = await Start(config, ws))
            {
                await session.RunTurnAsync("go", TestIssues.Make("1"), default);
            }

            Assert.Equal(TempWorkflow.NativePath(Path.GetFullPath(ws)), TempWorkflow.NativePath(Read("cwd.txt")), StringComparer.OrdinalIgnoreCase);
            var args = Runs()[0];
            Assert.Equal(["--output-format", "json"], args[..2]);
            foreach (var flag in new[] { "--no-ask-user", "--sync-session=off", "--disable-builtin-mcps", "--model=claude-sonnet-5.5", "--reasoning-effort=high",
                "--allow-tool=write", "--allow-tool=shell(git:*)", "--deny-tool=shell(git push)", "--excluded-tools=web_fetch", "--max-ai-credits=50" })
            {
                Assert.Contains(flag, args);
            }

            Assert.DoesNotContain("--allow-tool=tracker", args);
            Assert.DoesNotContain(args, a => a.StartsWith("--additional-mcp-config", StringComparison.Ordinal));

            var env = Read("env.txt");
            Assert.DoesNotContain("should-not-leak", env);
            Assert.DoesNotContain("ghp_should_not_leak", env);
            Assert.DoesNotContain("COPILOT_ALLOW_ALL=", env);
            Assert.Contains("COPILOT_DISABLE_LOGIN_SHELL_ENV=1", env);
            Assert.Contains("COPILOT_DISABLE_RESUME_AUTO_CD=1", env);
            Assert.Contains("COPILOT_AUTO_UPDATE=false", env);
            var home = Path.Combine(StateDir, "copilot-home");
            Assert.Contains($"COPILOT_HOME={home}", env);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_TRACKER_TOKEN", null);
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("COPILOT_ALLOW_ALL", null);
        }
    }

    [Fact]
    public async Task The_workspace_root_is_trusted_in_the_agent_home_and_other_settings_are_kept()
    {
        var home = Path.Combine(_tmp.Dir, "my-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.json"), "// User settings belong in settings.json.\n// This file is managed automatically.\n{\n  \"firstLaunchAt\": \"2026-11-20T22:52:59.334Z\",\n  \"trustedFolders\": [\"/elsewhere\"]\n}\n");
        var (config, ws) = Setup($"  home: \"{TempWorkflow.YamlPath(home)}\"");

        await using (var session = await Start(config, ws))
        {
            await session.RunTurnAsync("go", TestIssues.Make("1"), default);
        }

        var text = File.ReadAllText(Path.Combine(home, "config.json"));
        Assert.StartsWith("// User settings belong in settings.json.\n// This file is managed automatically.\n{", text);
        var json = JsonNode.Parse(string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("//", StringComparison.Ordinal))))!;
        Assert.Equal("2026-11-20T22:52:59.334Z", json["firstLaunchAt"]!.GetValue<string>());
        var trusted = json["trustedFolders"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal("/elsewhere", trusted[0]);
        Assert.Contains(config.WorkspaceRoot, trusted);
        Assert.Contains(CopilotHome.RealPath(config.WorkspaceRoot), trusted);
        Assert.Contains($"COPILOT_HOME={home}", Read("env.txt"));

        // Trusting again changes nothing.
        await CopilotHome.TrustAsync(home, config.WorkspaceRoot, default);
        Assert.Equal(text, File.ReadAllText(Path.Combine(home, "config.json")));
    }

    [Fact]
    public async Task A_resumed_session_reports_only_the_tokens_of_this_run()
    {
        var (config, ws) = Setup();
        string id;
        await using (var first = await Start(config, ws))
        {
            await first.RunTurnAsync("go", TestIssues.Make("1"), default);
            id = first.ThreadId!;
        }

        lock (_events) _events.Clear();
        await using (var resumed = await Start(config, ws, resume: id))
        {
            Assert.Equal(id, resumed.ThreadId);
            await resumed.RunTurnAsync("resume note", TestIssues.Make("1"), default);
        }

        Assert.Contains($"--resume={id}", Runs()[1]);
        // The session's file says 40 in / 14 out by now; the first run already counted 20 / 7.
        Assert.Equal(new TokenTotals(20, 7, 27, 1_500_000_000), Events().Single(e => e.Event == AgentEventNames.TurnCompleted).AbsoluteTotals);
    }

    [Fact]
    public async Task A_session_that_no_longer_exists_fails_as_port_exit_so_the_worker_starts_over()
    {
        var (config, ws) = Setup();
        await using var session = await Start(config, ws, resume: "gone-session");
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("resume note", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.PortExit, ex.Code);
        Assert.Contains("No session, task, or name matched", ex.Message);
    }

    [Fact]
    public async Task Tracker_tools_come_from_a_private_mcp_config_and_must_connect()
    {
        var (config, ws) = Setup();
        await using var gateway = new McpToolGateway(NullLogger<McpToolGateway>.Instance);
        await gateway.StartAsync(NullLoggerFactory.Instance, default);

        await using (var session = await Start(config, ws, gateway))
        {
            await session.RunTurnAsync("go", TestIssues.Make("1"), default);
            var args = Runs()[0];
            Assert.Contains("--allow-tool=tracker", args);
            var mcpPath = args.Single(a => a.StartsWith("--additional-mcp-config=@", StringComparison.Ordinal))["--additional-mcp-config=@".Length..];
            Assert.False(mcpPath.StartsWith(config.WorkspaceRoot, StringComparison.Ordinal), "MCP config must live outside the workspace");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(mcpPath));
            }

            var server = JsonNode.Parse(File.ReadAllText(mcpPath))!["mcpServers"]!["tracker"]!;
            Assert.Equal("http", server["type"]!.GetValue<string>());
            Assert.StartsWith("Bearer ", server["headers"]!["Authorization"]!.GetValue<string>(), StringComparison.Ordinal);

            // Copilot carries on without a server it can't reach, so the runner stops the turn itself.
            var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("MCP_NEEDS_AUTH", TestIssues.Make("1"), default));
            Assert.Equal(AgentErrorCodes.TurnFailed, ex.Code);
            Assert.Contains("status needs-auth", ex.Message);
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(StateDir, "mcp")));
    }

    [Fact]
    public async Task A_session_error_fails_the_turn()
    {
        var (config, ws) = Setup();
        await using var session = await Start(config, ws);
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("SESSION_ERROR", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.TurnFailed, ex.Code);
        Assert.Contains("quota: monthly quota reached", ex.Message);
    }

    [Fact]
    public async Task Process_exit_is_port_exit_with_stderr()
    {
        var (config, ws) = Setup();
        await using var session = await Start(config, ws);
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("EXIT_NOW", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.PortExit, ex.Code);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Silence_beyond_turn_timeout_fails()
    {
        var (config, ws) = Setup("  turn_timeout_ms: 300");
        await using var session = await Start(config, ws);
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("GO_SILENT", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.TurnTimeout, ex.Code);
    }

    [Fact]
    public async Task Missing_executable_is_agent_not_found()
    {
        var (config, ws) = Setup();
        config = config with { Copilot = config.Copilot! with { Command = "definitely-not-a-real-copilot-binary" } };
        await using var session = await Start(config, ws);
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("hi", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.AgentNotFound, ex.Code);
    }

    [Fact]
    public async Task Rejects_workspace_outside_root()
    {
        var (config, _) = Setup();
        var ex = await Assert.ThrowsAsync<AgentException>(() => Start(config, _tmp.Dir));
        Assert.Equal(AgentErrorCodes.InvalidWorkspaceCwd, ex.Code);
    }

    private static string OldSession(string home, string? id = null, int daysAgo = 30)
    {
        var dir = Path.Combine(home, "session-state", id ?? Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(file, "{}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-daysAgo));
        return dir;
    }

    [Fact]
    public async Task Sessions_unused_past_the_retention_are_deleted_from_symphonys_own_home()
    {
        var (config, ws) = Setup("  session_retention_days: 7");
        var home = Path.Combine(StateDir, "copilot-home");
        var old = OldSession(home);
        var recent = OldSession(home, daysAgo: 2);
        var notASession = OldSession(home, "notes");
        var baselines = Path.Combine(StateDir, "copilot-usage");
        Directory.CreateDirectory(baselines);
        var oldBaseline = Path.Combine(baselines, $"session-{Path.GetFileName(old).Replace("-", "", StringComparison.Ordinal)}.json");
        File.WriteAllText(oldBaseline, "{}");

        await using (await Start(config, ws))
        {
        }

        Assert.False(Directory.Exists(old));
        Assert.False(File.Exists(oldBaseline));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(notASession));
    }

    [Fact]
    public async Task A_configured_home_is_never_pruned()
    {
        var home = Path.Combine(_tmp.Dir, "my-home");
        var old = OldSession(home);
        var (config, ws) = Setup($"  home: \"{TempWorkflow.YamlPath(home)}\"\n  session_retention_days: 1");

        await using (await Start(config, ws))
        {
        }

        Assert.True(Directory.Exists(old));
    }

    public void Dispose() => _tmp.Dispose();
}

public class CopilotConfigTests
{
    private static ServiceConfig Build(TempWorkflow tmp, string extra)
    {
        tmp.Write($"tracker: {{kind: fake}}\n{extra}");
        return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(tmp.Path), tmp.Path);
    }

    [Fact]
    public void Copilot_settings_have_unattended_defaults()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var c = Build(tmp, "").Copilot!;

        Assert.Equal("copilot", c.Command);
        Assert.Null(c.Model);
        Assert.Equal(["write"], c.AllowTools);
        Assert.Empty(c.DenyTools);
        Assert.Null(c.Home);
        Assert.True(c.TrustWorkspaceRoot);
        Assert.True(c.Resume);
        Assert.Equal(300000, c.StallTimeoutMs);
        Assert.Null(c.MaxAiCredits);
        Assert.Equal(14, c.SessionRetentionDays);
    }

    [Fact]
    public void The_copilot_runner_routes_with_the_copilot_models_and_timeouts()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent: {runner: copilot}\nclaude: {model: opus}\ncopilot:\n  model: gpt-6-sol\n  model_by_state: {Todo: claude-sonnet-5.5}\n  stall_timeout_ms: 60000\n  home: ~/agent-home");

        Assert.Same(config.Copilot, config.RunnerSettings);
        Assert.Equal(new AgentRoute("copilot", "claude-sonnet-5.5"), config.RouteFor("Todo"));
        Assert.Equal(new AgentRoute("copilot", "gpt-6-sol"), config.RouteFor("In Progress"));
        Assert.Equal(60000, config.RunnerSettings.StallTimeoutMs);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agent-home"), config.Copilot!.Home);
    }

    [Fact]
    public void Stall_warnings_name_the_copilot_timeout()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var config = Build(tmp, "agent: {runner: copilot}\ncopilot:\n  stall_timeout_ms: 60000\ngates:\n  Done:\n    run: [\"true\"]\n    timeout_ms: 60000");

        Assert.Contains(config.Warnings, w => w.Contains("reaches copilot.stall_timeout_ms (60000)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("copilot: {reasoning_effort: extreme}", "copilot.reasoning_effort")]
    [InlineData("copilot: {command: \" \"}", "copilot.command")]
    [InlineData("copilot: {model_by_state: {Todo: \"\"}}", "copilot.model_by_state.Todo")]
    [InlineData("copilot: {max_ai_credits: 10}", "copilot.max_ai_credits")]
    [InlineData("copilot: {session_retention_days: -1}", "copilot.session_retention_days")]
    public void Bad_copilot_settings_are_config_errors(string yaml, string field)
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var ex = Assert.Throws<WorkflowException>(() => Build(tmp, yaml));
        Assert.Contains(field, ex.Message);
    }
}
