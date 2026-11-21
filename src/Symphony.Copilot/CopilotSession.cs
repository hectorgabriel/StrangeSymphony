using System.Diagnostics;
using System.Globalization;
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

namespace Symphony.Copilot;

/// <summary>
/// Runs GitHub Copilot CLI (<c>agent.runner: copilot</c>). The CLI runs unmodified and signs in however the host is
/// signed in (<c>copilot login</c>, or <c>COPILOT_GITHUB_TOKEN</c>); Symphony never reads Copilot credentials.
/// </summary>
public sealed class CopilotSessionFactory(IToolGateway? toolGateway, string privateStateDir, ILoggerFactory loggerFactory)
    : IAgentSessionFactory
{
    /// <summary>The agent home when <c>copilot.home</c> is not set.</summary>
    public string DefaultHome => Path.Combine(privateStateDir, "copilot-home");

    public async Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var config = request.Config;
        var settings = config.Copilot ?? throw new AgentException(AgentErrorCodes.AgentNotFound, "the workflow has no copilot settings");
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

        var home = settings.Home ?? DefaultHome;
        if (settings.Home is null && settings.SessionRetentionDays > 0)
        {
            PruneSessions(home, PrivateDir("copilot-usage"), TimeSpan.FromDays(settings.SessionRetentionDays));
        }

        if (settings.TrustWorkspaceRoot)
        {
            try
            {
                await CopilotHome.TrustAsync(home, config.WorkspaceRoot, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new AgentException(AgentErrorCodes.AgentNotFound, $"cannot set up the Copilot home {home}: {ex.Message}", ex);
            }
        }

        var session = new CopilotSession(request, settings, home, PrivateDir("copilot-usage"), onEvent, loggerFactory.CreateLogger<CopilotSession>());
        try
        {
            if (toolGateway is not null && request.Tracker.AgentToolSpecs().Count > 0)
            {
                var endpoint = toolGateway.Register(request.Tracker, () => session.CurrentIssue, request.Issue.Identifier);
                session.UseTools(endpoint, McpConfigFile.Write(privateStateDir, endpoint));
            }

            session.Start(request.ResumeSessionId ?? Guid.NewGuid().ToString());
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private readonly Lock _pruneGate = new();
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>
    /// Deletes sessions in Symphony's own agent home that haven't been used for <paramref name="retention"/>, with
    /// their token baselines. Copilot keeps every session forever, unencrypted. Runs at most once an hour.
    /// </summary>
    internal void PruneSessions(string home, string usageDir, TimeSpan retention)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_pruneGate)
        {
            if (now - _lastPrune < TimeSpan.FromHours(1))
            {
                return;
            }

            _lastPrune = now;
        }

        var sessions = Path.Combine(home, "session-state");
        if (!Directory.Exists(sessions))
        {
            return;
        }

        var logger = loggerFactory.CreateLogger<CopilotSessionFactory>();
        foreach (var dir in Directory.EnumerateDirectories(sessions))
        {
            var name = Path.GetFileName(dir);
            // Only session directories (UUIDs), and only by their newest file, so a long-idle but just-resumed one stays.
            if (!Guid.TryParse(name, out _))
            {
                continue;
            }

            try
            {
                var last = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir))
                    .Max();
                if (now - new DateTimeOffset(last, TimeSpan.Zero) < retention)
                {
                    continue;
                }

                Directory.Delete(dir, recursive: true);
                File.Delete(Path.Combine(usageDir, $"session-{string.Concat(name.Where(char.IsAsciiLetterOrDigit))}.json"));
                logger.LogInformation("action=copilot_session_prune outcome=deleted session_id={SessionId} last_used={LastUsed:o}", name, last);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("action=copilot_session_prune outcome=failed session_id={SessionId} error={Error}", name, ex.Message);
            }
        }
    }

    private string PrivateDir(string name)
    {
        var dir = Path.Combine(privateStateDir, name);
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }
}

public sealed class CopilotSession : IAgentSession
{
    private const int MaxLineChars = 10 * 1024 * 1024;
    private const string NoSuchSession = "No session, task, or name matched";

    private readonly AgentSessionRequest _request;
    private readonly CopilotSettings _settings;
    private readonly string _home;
    private readonly string _usageDir;
    private readonly Action<AgentEvent> _onEvent;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _toolNames = [];
    private ToolEndpoint? _endpoint;
    private string? _mcpConfigPath;
    private Process? _process;
    private bool _created;
    private int _turn;
    // Copilot's usage file counts the whole session, earlier Symphony runs included; this is where this run began.
    private TokenTotals _baseline = new(0, 0, 0, 0);

    internal CopilotSession(AgentSessionRequest request, CopilotSettings settings, string home, string usageDir, Action<AgentEvent> onEvent, ILogger logger)
    {
        _request = request;
        _settings = settings;
        _home = home;
        _usageDir = usageDir;
        _onEvent = onEvent;
        _logger = logger;
        CurrentIssue = request.Issue;
    }

    public string? ThreadId { get; private set; }

    internal Issue CurrentIssue { get; private set; }

    private bool HasTools => _endpoint is not null;

    private string? Pid => _process?.Id.ToString(CultureInfo.InvariantCulture);

    internal void UseTools(ToolEndpoint endpoint, string mcpConfigPath)
    {
        _endpoint = endpoint;
        _mcpConfigPath = mcpConfigPath;
    }

    internal void Start(string sessionId)
    {
        ThreadId = sessionId;
        _created = _request.ResumeSessionId is not null;
        if (_created)
        {
            _baseline = ReadBaseline(sessionId);
        }

        _onEvent(new AgentEvent(AgentEventNames.SessionStarted, DateTimeOffset.UtcNow)
        {
            ThreadId = sessionId,
            Message = $"copilot session {sessionId} {(_created ? "resumed" : "started")} in {_request.WorkspacePath} with model {_request.EffectiveRoute.Model ?? "default"}",
        });
    }

    public async Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct)
    {
        var sessionId = ThreadId ?? throw new InvalidOperationException("session not started");
        CurrentIssue = currentIssue;
        _turn++;
        var usagePath = Path.Combine(_usageDir, $"{Guid.NewGuid():N}.json");
        try
        {
            await RunProcessAsync(prompt, sessionId, usagePath, ct);
        }
        finally
        {
            TryDelete(usagePath);
        }
    }

    private async Task RunProcessAsync(string prompt, string sessionId, string usagePath, CancellationToken ct)
    {
        var config = _request.Config;
        var secretNames = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN") is { Length: > 0 } ? new[] { "COPILOT_GITHUB_TOKEN" } : [];
        var args = CopilotCommandLine.BuildArgs(_settings, _request.EffectiveRoute.Model, sessionId, _created, _mcpConfigPath, HasTools, usagePath, secretNames);
        var psi = AgentProcess.StartInfo(_settings.Command, args, Path.GetFullPath(_request.WorkspacePath), "copilot.command", config.Hooks.Shell);
        EnvironmentFilter.Remove(psi.Environment, [.. _request.Tracker.SecretEnvironmentNames(), .. CopilotCommandLine.AlwaysStripped, .. config.StrippedEnvironment]);
        foreach (var (name, value) in CopilotCommandLine.FixedEnvironment(_home))
        {
            psi.Environment[name] = value;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
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
        var messages = Channel.CreateUnbounded<JsonObject?>(new UnboundedChannelOptions { SingleReader = true });
        var stderr = new StringBuilder();
        var stdoutPump = Task.Run(() => PumpStdoutAsync(process, messages.Writer), CancellationToken.None);
        var stderrPump = Task.Run(() => PumpStderrAsync(process, stderr), CancellationToken.None);
        var turn = new TurnState();
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It exited before reading the prompt; the exit code and stderr below say why.
            }

            while (true)
            {
                JsonObject? message;
                using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // Silence timeout: each line resets it (SPEC 10.6).
                    silence.CancelAfter(_settings.TurnTimeoutMs);
                    try
                    {
                        message = await messages.Reader.ReadAsync(silence.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Emit(AgentEventNames.TurnFailed, $"no output for {_settings.TurnTimeoutMs} ms");
                        throw new AgentException(AgentErrorCodes.TurnTimeout, $"turn produced no output for {_settings.TurnTimeoutMs} ms");
                    }
                    catch (ChannelClosedException)
                    {
                        message = null;
                    }
                }

                if (message is null)
                {
                    break;
                }

                Handle(message, turn);
            }

            await WaitForExitQuietly(process);
            await stderrPump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            Finish(process, turn, Tail(stderr), usagePath);
        }
        finally
        {
            ShellRunner.KillTree(process);
            await stdoutPump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            _process = null;
        }
    }

    private sealed class TurnState
    {
        public string? LastText { get; set; }
        public string? Error { get; set; }
        public int? ResultExitCode { get; set; }
        public int Denials { get; set; }
    }

    /// <summary>Maps one Copilot session event to Symphony events. Throws when the turn can't go on.</summary>
    private void Handle(JsonObject message, TurnState turn)
    {
        var type = Str(message["type"]);
        var data = message["data"] as JsonObject;
        var ephemeral = message["ephemeral"] is JsonValue e && e.TryGetValue(out bool b) && b;
        switch (type)
        {
            case "session.start" or "session.resume":
                _created = true;
                if (Str(data?["sessionId"]) is { } sid)
                {
                    ThreadId = sid;
                }

                _onEvent(new AgentEvent(AgentEventNames.OtherMessage, DateTimeOffset.UtcNow) { ProcessId = Pid, ThreadId = ThreadId, Message = type });
                return;

            case "session.mcp_servers_loaded" when HasTools:
            {
                var tracker = (data?["servers"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(s => Str(s["name"]) == McpConfigFile.TrackerServerName);
                var status = Str(tracker?["status"]) ?? "missing";
                if (status != "connected")
                {
                    // Copilot would go on without the tracker tools and exit 0, so the issue could never move.
                    var reason = $"the tracker MCP server did not connect (status {status}); check that Copilot CLI and MCP servers are allowed for this account";
                    Emit(AgentEventNames.TurnEndedWithError, reason);
                    throw new AgentException(AgentErrorCodes.TurnFailed, reason);
                }

                Emit(AgentEventNames.OtherMessage, "tracker tools connected");
                return;
            }

            case "assistant.message":
                if (Str(data?["content"]) is { Length: > 0 } text)
                {
                    turn.LastText = text;
                    Emit(AgentEventNames.Notification, Truncate(text));
                }

                return;

            case "tool.execution_start":
            {
                var name = Str(data?["toolName"]) ?? "unknown";
                if (Str(data?["toolCallId"]) is { } id)
                {
                    _toolNames[id] = name;
                }

                Emit(AgentEventNames.ToolCall, $"tool:{name}");
                return;
            }

            case "tool.execution_complete":
                if (data?["success"] is JsonValue ok && ok.TryGetValue(out bool success) && !success)
                {
                    var name = Str(data["toolCallId"]) is { } id && _toolNames.TryGetValue(id, out var n) ? n : "tool";
                    var error = Str(data["error"]?["message"]) ?? "failed";
                    if (error.StartsWith("Permission denied", StringComparison.Ordinal))
                    {
                        turn.Denials++;
                    }

                    Emit(AgentEventNames.Notification, $"{name} failed: {Truncate(error, 300)}");
                }

                return;

            case "session.warning":
                Emit(AgentEventNames.Notification, $"warning: {Truncate(Str(data?["message"]), 500)}");
                return;

            case "session.error":
                turn.Error = $"{Str(data?["errorType"]) ?? "error"}: {Str(data?["message"]) ?? "unknown error"}";
                Emit(AgentEventNames.Notification, Truncate(turn.Error));
                return;

            case "session.quota_observation":
                _onEvent(new AgentEvent(AgentEventNames.RateLimit, DateTimeOffset.UtcNow) { ProcessId = Pid, RateLimits = data?.DeepClone() });
                return;

            case "result":
                turn.ResultExitCode = message["exitCode"] is JsonValue c && c.TryGetValue(out int code) ? code : null;
                return;

            default:
                // Streaming deltas and other ephemeral events only keep the silence timer from firing.
                if (!ephemeral)
                {
                    Emit(AgentEventNames.OtherMessage, type);
                }

                return;
        }
    }

    private void Finish(Process process, TurnState turn, string stderr, string usagePath)
    {
        var exit = process.HasExited ? process.ExitCode : -1;
        var totals = ReadUsage(usagePath);
        var turnId = _turn.ToString(CultureInfo.InvariantCulture);
        if (exit == 0 && turn.Error is null && turn.ResultExitCode is null or 0)
        {
            _onEvent(new AgentEvent(AgentEventNames.TurnCompleted, DateTimeOffset.UtcNow)
            {
                ProcessId = Pid,
                ThreadId = ThreadId,
                TurnId = turnId,
                Message = turn.Denials > 0 ? $"{Truncate(turn.LastText)} [permission denials: {turn.Denials}]" : Truncate(turn.LastText),
                AbsoluteTotals = totals,
            });
            return;
        }

        if (turn.Error is { } error)
        {
            _onEvent(new AgentEvent(AgentEventNames.TurnEndedWithError, DateTimeOffset.UtcNow) { ProcessId = Pid, ThreadId = ThreadId, TurnId = turnId, Message = error, AbsoluteTotals = totals });
            throw new AgentException(AgentErrorCodes.TurnFailed, error);
        }

        var description = $"agent process exited (code {exit}){(stderr.Length > 0 ? $": {Truncate(stderr)}" : "")}";
        Emit(AgentEventNames.TurnFailed, description);
        var notFound = exit == 127 || (exit == 1 && stderr.Contains("CommandNotFoundException", StringComparison.Ordinal));
        if (stderr.Contains(NoSuchSession, StringComparison.Ordinal))
        {
            // The saved session is gone; port_exit lets the worker start a new one (resume fallback).
            throw new AgentException(AgentErrorCodes.PortExit, description);
        }

        throw new AgentException(notFound ? AgentErrorCodes.AgentNotFound : AgentErrorCodes.PortExit, description);
    }

    /// <summary>
    /// Session totals since this run began, from <c>--usage-output-file</c>. Input counts cache reads and writes,
    /// as for Claude. The file is cumulative for the session, so the last total is also saved as the next run's baseline.
    /// </summary>
    private TokenTotals? ReadUsage(string usagePath)
    {
        JsonObject? usage;
        try
        {
            usage = File.Exists(usagePath) ? JsonNode.Parse(File.ReadAllText(usagePath)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning("action=copilot_usage outcome=unreadable issue_identifier={IssueIdentifier} error={Error}", _request.Issue.Identifier, ex.Message);
            usage = null;
        }

        if (usage?["tokenDetails"] is not JsonObject details)
        {
            return null;
        }

        long Count(string key) => Long(details[key]?["tokenCount"]);
        var input = Count("input") + Count("cache_read") + Count("cache_write");
        var output = Count("output");
        var nanoAiu = Long(usage["totalNanoAiu"]);
        if (ThreadId is { } sid)
        {
            SaveBaseline(sid, input, output, nanoAiu);
        }

        input = Math.Max(0, input - _baseline.InputTokens);
        output = Math.Max(0, output - _baseline.OutputTokens);
        return new TokenTotals(input, output, input + output, Math.Max(0, nanoAiu - _baseline.NanoAiu));
    }

    private string BaselinePath(string sessionId) =>
        Path.Combine(_usageDir, $"session-{string.Concat(sessionId.Where(char.IsAsciiLetterOrDigit))}.json");

    private TokenTotals ReadBaseline(string sessionId)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(BaselinePath(sessionId)));
            var input = Long(node?["input"]);
            var output = Long(node?["output"]);
            return new TokenTotals(input, output, input + output, Long(node?["nano_aiu"]));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unknown (a session from before this file existed, or another machine): its earlier tokens count again once.
            return new TokenTotals(0, 0, 0, 0);
        }
    }

    private void SaveBaseline(string sessionId, long input, long output, long nanoAiu)
    {
        try
        {
            File.WriteAllText(BaselinePath(sessionId), new JsonObject { ["input"] = input, ["output"] = output, ["nano_aiu"] = nanoAiu }.ToJsonString());
        }
        catch (IOException ex)
        {
            _logger.LogWarning("action=copilot_usage outcome=baseline_not_saved session_id={SessionId} error={Error}", sessionId, ex.Message);
        }
    }

    private async Task PumpStdoutAsync(Process process, ChannelWriter<JsonObject?> writer)
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

                writer.TryWrite(obj);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync(Process process, StringBuilder tail)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                lock (tail)
                {
                    tail.AppendLine(line);
                    if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                }

                _logger.LogDebug("action=agent_stderr issue_identifier={IssueIdentifier} line={Line}", _request.Issue.Identifier, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static string Tail(StringBuilder tail)
    {
        lock (tail)
        {
            return tail.ToString().Trim();
        }
    }

    private static async Task WaitForExitQuietly(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }
    }

    private void Emit(string name, string? message) =>
        _onEvent(new AgentEvent(name, DateTimeOffset.UtcNow) { ProcessId = Pid, Message = message });

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            ShellRunner.KillTree(process);
        }

        _endpoint?.Registration.Dispose();
        if (_mcpConfigPath is not null)
        {
            TryDelete(_mcpConfigPath);
        }

        return ValueTask.CompletedTask;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out long l) ? l : node is JsonValue d && d.TryGetValue(out double x) ? (long)x : 0;

    private static string? Truncate(string? s, int max = 1000) => s is null || s.Length <= max ? s : s[..max] + "…";
}
