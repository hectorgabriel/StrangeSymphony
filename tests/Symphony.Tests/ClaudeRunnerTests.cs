using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Claude;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>
/// Drives ClaudeCodeSession against a fake `claude` that speaks the stream-json protocol. The fake
/// is a bash script, so these tests are POSIX-only.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ClaudeRunnerTests : IDisposable
{
    private const string FakeClaude = """
        #!/usr/bin/env bash
        OUT="$(dirname "$0")"
        printf '%s\n' "$@" > "$OUT/args.txt"
        pwd > "$OUT/cwd.txt"
        env > "$OUT/env.txt"
        SID=""
        while [ $# -gt 0 ]; do [ "$1" = "--session-id" ] && SID="$2"; shift; done
        turn=0
        while IFS= read -r line; do
          turn=$((turn+1))
          echo "$line" >> "$OUT/stdin.txt"
          echo '{"type":"system","subtype":"init","session_id":"'"$SID"'"}'
          echo 'this is not json'
          case "$line" in
            *EXIT_NOW*) echo "fatal: boom" >&2; exit 3 ;;
            *GO_SILENT*) sleep 30 ;;
            *FAIL_TURN*) echo '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"it broke","usage":{"input_tokens":1,"output_tokens":1}}'; continue ;;
          esac
          echo '{"type":"assistant","message":{"content":[{"type":"text","text":"working '"$turn"'"},{"type":"tool_use","name":"Bash"}]}}'
          echo '{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour"}}'
          echo '{"type":"result","subtype":"success","is_error":false,"result":"done '"$turn"'","usage":{"input_tokens":10,"cache_creation_input_tokens":5,"cache_read_input_tokens":5,"output_tokens":7},"permission_denials":[]}'
        done
        """;

    private readonly TempWorkflow _tmp;
    private readonly string _bin;
    private readonly List<AgentEvent> _events = [];
    private readonly FakeTracker _tracker = new();

    public ClaudeRunnerTests()
    {
        _tmp = new TempWorkflow("tracker: {kind: fake}");
        _bin = Path.Combine(_tmp.Dir, "bin");
        Directory.CreateDirectory(_bin);
        var script = Path.Combine(_bin, "fake-claude");
        File.WriteAllText(script, FakeClaude.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (ServiceConfig Config, string Workspace) Setup(string claudeYaml = "")
    {
        _tmp.Write($"tracker: {{kind: fake}}\nworkspace: {{root: \"{{ROOT}}\"}}\nclaude:\n  command: \"{Path.Combine(_bin, "fake-claude")}\"\n  model: sonnet\n  allowed_tools: [\"Bash(git *)\", Edit]\n{claudeYaml}");
        var config = new ConfigBuilder([new FakeTrackerFactory(_tracker)], _ => null).Build(WorkflowLoader.Load(_tmp.Path), _tmp.Path);
        var ws = Path.Combine(config.WorkspaceRoot, "WI-1");
        Directory.CreateDirectory(ws);
        return (config, ws);
    }

    private async Task<IAgentSession> Start(ServiceConfig config, string ws, IToolGateway? gateway = null) =>
        await new ClaudeCodeSessionFactory(gateway, Path.Combine(_tmp.Dir, "state"), NullLoggerFactory.Instance)
            .StartAsync(new AgentSessionRequest(TestIssues.Make("1"), ws, config, _tracker), e => { lock (_events) _events.Add(e); }, default);

    private string Read(string file) => File.ReadAllText(Path.Combine(_bin, file));

    [Fact]
    public async Task Runs_multiple_turns_on_one_process_and_reports_absolute_tokens()
    {
        var (config, ws) = Setup();
        await using (var session = await Start(config, ws))
        {
            await session.RunTurnAsync("first prompt", TestIssues.Make("1"), default);
            await session.RunTurnAsync("continue please", TestIssues.Make("1"), default);
            Assert.NotNull(session.ThreadId);
        }

        var stdin = File.ReadAllLines(Path.Combine(_bin, "stdin.txt"));
        Assert.Equal(2, stdin.Length);
        Assert.Equal("first prompt", JsonNode.Parse(stdin[0])!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("user", JsonNode.Parse(stdin[1])!["type"]!.GetValue<string>());

        List<AgentEvent> events;
        lock (_events) events = _events.ToList();
        Assert.Equal(AgentEventNames.SessionStarted, events[0].Event);
        Assert.NotNull(events[0].ProcessId);
        var completed = events.Where(e => e.Event == AgentEventNames.TurnCompleted).ToList();
        Assert.Equal(2, completed.Count);
        Assert.Equal(new TokenTotals(20, 7, 27), completed[0].AbsoluteTotals);
        Assert.Equal(new TokenTotals(40, 14, 54), completed[1].AbsoluteTotals);
        Assert.Equal(["1", "2"], completed.Select(e => e.TurnId));
        Assert.Contains(events, e => e.Event == AgentEventNames.Malformed);
        Assert.Contains(events, e => e.Event == AgentEventNames.ToolCall && e.Message == "tool:Bash");
        Assert.Contains(events, e => e.Event == AgentEventNames.RateLimit && e.RateLimits!["rateLimitType"]!.GetValue<string>() == "five_hour");
        Assert.Contains(events, e => e.Event == AgentEventNames.Notification && e.Message == "working 1");
    }

    [Fact]
    public async Task Launches_in_workspace_with_expected_flags_and_without_tracker_secrets()
    {
        Environment.SetEnvironmentVariable("FAKE_TRACKER_TOKEN", "should-not-leak");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        try
        {
            var (config, ws) = Setup();
            await using (var session = await Start(config, ws))
            {
                await session.RunTurnAsync("go", TestIssues.Make("1"), default);
            }

            Assert.Equal(Path.GetFullPath(ws).Replace("/private/", "/"), Read("cwd.txt").Trim().Replace("/private/", "/"));
            var env = Read("env.txt");
            Assert.DoesNotContain("should-not-leak", env);
            Assert.DoesNotContain("CLAUDECODE=", env);

            var args = File.ReadAllLines(Path.Combine(_bin, "args.txt"));
            string After(string flag) => args[Array.IndexOf(args, flag) + 1];
            Assert.Equal("-p", args[0]);
            Assert.Equal("stream-json", After("--input-format"));
            Assert.Equal("stream-json", After("--output-format"));
            Assert.Equal("acceptEdits", After("--permission-mode"));
            Assert.Equal("none", After("--permission-prompts"));
            Assert.Equal("sonnet", After("--model"));
            Assert.Equal("WI-1: Title 1", After("--name"));
            Assert.Equal("Bash(git *)", After("--allowedTools"));
            Assert.Equal("AskUserQuestion", After("--disallowedTools"));
            Assert.Contains("--strict-mcp-config", args);
            Assert.True(Guid.TryParse(After("--session-id"), out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_TRACKER_TOKEN", null);
            Environment.SetEnvironmentVariable("CLAUDECODE", null);
        }
    }

    [Fact]
    public async Task Error_result_fails_the_turn()
    {
        var (config, ws) = Setup();
        await using var session = await Start(config, ws);
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.RunTurnAsync("FAIL_TURN", TestIssues.Make("1"), default));
        Assert.Equal(AgentErrorCodes.TurnFailed, ex.Code);
        Assert.Contains("it broke", ex.Message);
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
        config = config with { Claude = config.Claude with { Command = "definitely-not-a-real-claude-binary" } };
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

    [Fact]
    public async Task Tracker_tools_are_served_over_mcp_with_session_auth()
    {
        var (config, ws) = Setup();
        await using var gateway = new McpToolGateway(NullLogger<McpToolGateway>.Instance);
        await gateway.StartAsync(NullLoggerFactory.Instance, default);

        await using (var session = await Start(config, ws, gateway))
        {
            await session.RunTurnAsync("go", TestIssues.Make("1"), default);
            var args = File.ReadAllLines(Path.Combine(_bin, "args.txt"));
            var mcpPath = args[Array.IndexOf(args, "--mcp-config") + 1];
            Assert.Contains("mcp__tracker", args);
            Assert.False(mcpPath.StartsWith(config.WorkspaceRoot, StringComparison.Ordinal), "MCP config must live outside the workspace");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(mcpPath));

            var server = JsonNode.Parse(File.ReadAllText(mcpPath))!["mcpServers"]!["tracker"]!;
            var url = server["url"]!.GetValue<string>();
            var auth = server["headers"]!["Authorization"]!.GetValue<string>();
            Assert.DoesNotContain("should-not-leak", auth);

            using var http = new HttpClient();
            async Task<JsonNode> Rpc(string method, JsonObject? @params, string? authHeader = null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = @params }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader ?? auth);
                using var res = await http.SendAsync(req);
                return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            }

            async Task<JsonNode> Raw(string body)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = AuthenticationHeaderValue.Parse(auth);
                using var res = await http.SendAsync(req);
                Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
                return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            }

            Assert.Equal("symphony-tracker", (await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }))["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Equal("2025-03-26", (await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }))["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("2025-06-18", (await Rpc("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }))["result"]!["protocolVersion"]!.GetValue<string>());

            // Malformed shapes are JSON-RPC errors or tool errors, never HTTP 500s.
            Assert.Equal(-32600, (await Raw("""{"jsonrpc":"2.0","id":2,"method":5}"""))["error"]!["code"]!.GetValue<int>());
            Assert.True((await Raw("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":[1]}"""))["result"]!["isError"]!.GetValue<bool>());
            Assert.True((await Raw("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":7}}"""))["result"]!["isError"]!.GetValue<bool>());
            Assert.Equal("2025-06-18", (await Raw("""{"jsonrpc":"2.0","id":5,"method":"initialize","params":{"protocolVersion":1}}"""))["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("echo", (await Rpc("tools/list", null))["result"]!["tools"]![0]!["name"]!.GetValue<string>());

            var call = await Rpc("tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["x"] = 1 } });
            Assert.False(call["result"]!["isError"]!.GetValue<bool>());
            Assert.Equal("WI-1", call["result"]!["structuredContent"]!["issue"]!.GetValue<string>());

            var unsupported = await Rpc("tools/call", new JsonObject { ["name"] = "nope" });
            Assert.True(unsupported["result"]!["isError"]!.GetValue<bool>());

            Assert.Equal("unauthorized", (await Rpc("tools/list", null, "Bearer wrong"))["error"]!["code"]!.GetValue<string>());
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(_tmp.Dir, "state", "mcp")));
    }

    public void Dispose() => _tmp.Dispose();
}
