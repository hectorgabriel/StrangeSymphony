using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Util;
using Symphony.Core.Workspaces;

namespace Symphony.Claude;

/// <summary>
/// Launches Claude Code per worker. The CLI runs unmodified and authenticates however the host
/// is signed in (the operator's own Claude subscription login, or <c>ANTHROPIC_API_KEY</c>).
/// Symphony never reads or stores Claude credentials.
/// </summary>
public sealed class ClaudeCodeSessionFactory(IToolGateway? toolGateway, string privateStateDir, ILoggerFactory loggerFactory)
    : IAgentSessionFactory
{
    /// <summary>Env names that must not reach the agent in addition to the adapter's secret names.</summary>
    private static readonly string[] AlwaysStripped = ["CLAUDECODE"];

    public async Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var config = request.Config;
        var cwd = Path.GetFullPath(request.WorkspacePath);
        try
        {
            WorkspaceManager.EnsureInsideRoot(config.WorkspaceRoot, cwd);
        }
        catch (WorkspaceException ex)
        {
            throw new AgentException(AgentErrorCodes.InvalidWorkspaceCwd, ex.Message, ex);
        }

        if (!Directory.Exists(cwd))
        {
            throw new AgentException(AgentErrorCodes.InvalidWorkspaceCwd, $"workspace {cwd} does not exist");
        }

        var session = new ClaudeCodeSession(request, onEvent, loggerFactory.CreateLogger<ClaudeCodeSession>());
        try
        {
            ToolEndpoint? endpoint = null;
            string? mcpConfigPath = null;
            var hasTools = toolGateway is not null && request.Tracker.AgentToolSpecs().Count > 0;
            if (hasTools)
            {
                endpoint = toolGateway!.Register(request.Tracker, () => session.CurrentIssue, request.Issue.Identifier);
                mcpConfigPath = WriteMcpConfig(endpoint);
            }

            var sessionId = Guid.NewGuid().ToString();
            var args = ClaudeCommandLine.BuildArgs(config.Claude, sessionId, mcpConfigPath, hasTools, $"{request.Issue.Identifier}: {request.Issue.Title}");
            var psi = ClaudeCommandLine.StartInfo(config.Claude.Command, args, cwd);
            foreach (var name in request.Tracker.SecretEnvironmentNames().Concat(AlwaysStripped))
            {
                psi.Environment.Remove(name);
            }

            await session.StartAsync(psi, sessionId, endpoint, mcpConfigPath, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private string WriteMcpConfig(ToolEndpoint endpoint)
    {
        var dir = Path.Combine(privateStateDir, "mcp");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.json");
        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ClaudeCommandLine.TrackerMcpServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = endpoint.Url,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {endpoint.BearerToken}" },
                },
            },
        };

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json.ToJsonString());
        }

        return path;
    }
}

public sealed class ClaudeCodeSession : IAgentSession
{
    private const int MaxLineChars = 10 * 1024 * 1024;

    private readonly AgentSessionRequest _request;
    private readonly Action<AgentEvent> _onEvent;
    private readonly ILogger _logger;
    private readonly Channel<JsonObject?> _messages = Channel.CreateUnbounded<JsonObject?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StringBuilder _stderrTail = new();
    private Process? _process;
    private ToolEndpoint? _endpoint;
    private string? _mcpConfigPath;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private int _turn;
    private long _input;
    private long _output;

    internal ClaudeCodeSession(AgentSessionRequest request, Action<AgentEvent> onEvent, ILogger logger)
    {
        _request = request;
        _onEvent = onEvent;
        _logger = logger;
        CurrentIssue = request.Issue;
    }

    public string? ThreadId { get; private set; }

    internal Issue CurrentIssue { get; private set; }

    private string? Pid => _process?.Id.ToString();

    internal Task StartAsync(ProcessStartInfo psi, string sessionId, ToolEndpoint? endpoint, string? mcpConfigPath, CancellationToken ct)
    {
        _endpoint = endpoint;
        _mcpConfigPath = mcpConfigPath;
        ThreadId = sessionId;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Emit(AgentEventNames.StartupFailed, ex.Message);
            throw new AgentException(AgentErrorCodes.AgentNotFound, $"cannot launch agent shell: {ex.Message}", ex);
        }

        _process = process;
        _stdoutPump = Task.Run(() => PumpStdoutAsync(process));
        _stderrPump = Task.Run(() => PumpStderrAsync(process));

        _onEvent(new AgentEvent(AgentEventNames.SessionStarted, DateTimeOffset.UtcNow)
        {
            ProcessId = Pid,
            ThreadId = sessionId,
            Message = $"claude session {sessionId} started in {_request.WorkspacePath}",
        });
        return Task.CompletedTask;
    }

    public async Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct)
    {
        var process = _process ?? throw new InvalidOperationException("session not started");
        CurrentIssue = currentIssue;
        _turn++;

        var userMessage = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
        };

        try
        {
            await process.StandardInput.WriteLineAsync(userMessage.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            throw await ExitFailureAsync(process, ex);
        }

        var turnTimeoutMs = _request.Config.Claude.TurnTimeoutMs;
        while (true)
        {
            JsonObject? message;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Silence timeout: each message resets it (SPEC 10.6).
                silence.CancelAfter(turnTimeoutMs);
                try
                {
                    message = await _messages.Reader.ReadAsync(silence.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Emit(AgentEventNames.TurnFailed, $"no output for {turnTimeoutMs} ms");
                    ShellRunner.KillTree(process);
                    throw new AgentException(AgentErrorCodes.TurnTimeout, $"turn produced no output for {turnTimeoutMs} ms");
                }
                catch (ChannelClosedException)
                {
                    message = null;
                }
            }

            if (message is null)
            {
                throw await ExitFailureAsync(process, null);
            }

            if (HandleMessage(message))
            {
                return;
            }
        }
    }

    /// <summary>Maps one stream-json message to events. Returns true when the current turn ended successfully.</summary>
    private bool HandleMessage(JsonObject message)
    {
        var type = Str(message["type"]);
        switch (type)
        {
            case "system":
            {
                var subtype = Str(message["subtype"]);
                if (subtype == "init" && Str(message["session_id"]) is { } sid)
                {
                    ThreadId = sid;
                }

                Emit(AgentEventNames.OtherMessage, $"system:{subtype}");
                return false;
            }

            case "assistant":
            {
                var content = message["message"]?["content"] as JsonArray;
                var summary = new List<string>();
                foreach (var block in content?.OfType<JsonObject>() ?? [])
                {
                    switch (Str(block["type"]))
                    {
                        case "text" when Str(block["text"]) is { Length: > 0 } text:
                            summary.Add(text);
                            break;
                        case "tool_use":
                            Emit(AgentEventNames.ToolCall, $"tool:{Str(block["name"])}");
                            break;
                    }
                }

                if (summary.Count > 0)
                {
                    Emit(AgentEventNames.Notification, string.Join("\n", summary));
                }

                return false;
            }

            case "rate_limit_event":
                _onEvent(new AgentEvent(AgentEventNames.RateLimit, DateTimeOffset.UtcNow)
                {
                    ProcessId = Pid,
                    RateLimits = message["rate_limit_info"]?.DeepClone(),
                });
                return false;

            case "result":
                return HandleResult(message);

            default:
                Emit(AgentEventNames.OtherMessage, type is null ? null : $"{type}");
                return false;
        }
    }

    private bool HandleResult(JsonObject result)
    {
        // Each result carries usage for the turn that just ended; sum into absolute session totals.
        if (result["usage"] is JsonObject usage)
        {
            _input += Long(usage["input_tokens"]) + Long(usage["cache_creation_input_tokens"]) + Long(usage["cache_read_input_tokens"]);
            _output += Long(usage["output_tokens"]);
        }

        var subtype = Str(result["subtype"]);
        var isError = result["is_error"] is JsonValue v && v.TryGetValue(out bool b) && b;
        var denials = (result["permission_denials"] as JsonArray)?.Count ?? 0;
        var text = Str(result["result"]);
        var totals = new TokenTotals(_input, _output, _input + _output);

        if (!isError && subtype == "success")
        {
            _onEvent(new AgentEvent(AgentEventNames.TurnCompleted, DateTimeOffset.UtcNow)
            {
                ProcessId = Pid,
                ThreadId = ThreadId,
                TurnId = _turn.ToString(),
                Message = denials > 0 ? $"{Truncate(text)} [permission denials: {denials}]" : Truncate(text),
                AbsoluteTotals = totals,
            });
            return true;
        }

        var reason = $"{subtype ?? "error"}{(text is null ? "" : $": {Truncate(text)}")}";
        _onEvent(new AgentEvent(AgentEventNames.TurnEndedWithError, DateTimeOffset.UtcNow)
        {
            ProcessId = Pid,
            ThreadId = ThreadId,
            TurnId = _turn.ToString(),
            Message = reason,
            AbsoluteTotals = totals,
        });
        throw new AgentException(AgentErrorCodes.TurnFailed, reason);
    }

    private async Task PumpStdoutAsync(Process process)
    {
        var reader = new BoundedLineReader(process.StandardOutput, MaxLineChars);
        try
        {
            while (true)
            {
                var (line, oversized) = await reader.ReadLineAsync(CancellationToken.None);
                if (oversized)
                {
                    Emit(AgentEventNames.Malformed, "stdout line exceeded 10 MB and was discarded");
                    continue;
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonObject? obj = null;
                try
                {
                    obj = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                }

                if (obj is null)
                {
                    Emit(AgentEventNames.Malformed, Truncate(line));
                    continue;
                }

                _messages.Writer.TryWrite(obj);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }

                _logger.LogDebug("action=agent_stderr issue_identifier={IssueIdentifier} line={Line}", _request.Issue.Identifier, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private string ExitDescription()
    {
        string tail;
        lock (_stderrTail)
        {
            tail = _stderrTail.ToString().Trim();
        }

        var code = _process is { HasExited: true } p ? p.ExitCode.ToString() : "unknown";
        return $"agent process exited (code {code}){(tail.Length > 0 ? $": {Truncate(tail)}" : "")}";
    }

    /// <summary>Maps an exited agent process to <c>agent_not_found</c> (shell exit 127) or <c>port_exit</c>.</summary>
    private async Task<AgentException> ExitFailureAsync(Process process, Exception? inner)
    {
        await WaitForExitQuietly(process);
        if (_stderrPump is not null) await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        var description = ExitDescription();
        Emit(AgentEventNames.TurnFailed, description);
        var notFound = process.HasExited && process.ExitCode == 127;
        return new AgentException(notFound ? AgentErrorCodes.AgentNotFound : AgentErrorCodes.PortExit, description, inner);
    }

    private static async Task WaitForExitQuietly(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
    }

    private void Emit(string name, string? message) =>
        _onEvent(new AgentEvent(name, DateTimeOffset.UtcNow) { ProcessId = Pid, Message = message });

    public async ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            try
            {
                // Closing stdin ends the stream-json session gracefully.
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
            }

            ShellRunner.KillTree(process);
            if (_stdoutPump is not null) await _stdoutPump.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
            if (_stderrPump is not null) await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
            process.Dispose();
            _process = null;
        }

        _endpoint?.Registration.Dispose();
        if (_mcpConfigPath is not null)
        {
            try
            {
                File.Delete(_mcpConfigPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out long l) ? l : node is JsonValue d && d.TryGetValue(out double x) ? (long)x : 0;

    private static string? Truncate(string? s, int max = 1000) => s is null || s.Length <= max ? s : s[..max] + "…";
}
