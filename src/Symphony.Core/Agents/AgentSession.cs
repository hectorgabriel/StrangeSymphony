using System.Text.Json.Nodes;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Core.Agents;

/// <summary>Absolute (cumulative) token totals for the current agent session.</summary>
public sealed record TokenTotals(long InputTokens, long OutputTokens, long TotalTokens);

/// <summary>Structured runtime event emitted by the agent client to the orchestrator (SPEC 10.4).</summary>
public sealed record AgentEvent(string Event, DateTimeOffset Timestamp)
{
    public string? ProcessId { get; init; }
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    public string? Message { get; init; }
    public TokenTotals? AbsoluteTotals { get; init; }
    public JsonNode? RateLimits { get; init; }
}

public static class AgentEventNames
{
    public const string SessionStarted = "session_started";
    public const string StartupFailed = "startup_failed";
    public const string TurnStarted = "turn_started";
    public const string TurnCompleted = "turn_completed";
    public const string TurnFailed = "turn_failed";
    public const string TurnCancelled = "turn_cancelled";
    public const string TurnEndedWithError = "turn_ended_with_error";
    public const string TurnInputRequired = "turn_input_required";
    public const string ToolCall = "tool_call";
    public const string UnsupportedToolCall = "unsupported_tool_call";
    public const string Notification = "notification";
    public const string RateLimit = "rate_limit";
    public const string OtherMessage = "other_message";
    public const string Malformed = "malformed";
}

/// <summary>Normalized agent error categories (SPEC 10.6).</summary>
public static class AgentErrorCodes
{
    public const string AgentNotFound = "agent_not_found";
    public const string InvalidWorkspaceCwd = "invalid_workspace_cwd";
    public const string ResponseTimeout = "response_timeout";
    public const string TurnTimeout = "turn_timeout";
    public const string PortExit = "port_exit";
    public const string ResponseError = "response_error";
    public const string TurnFailed = "turn_failed";
    public const string TurnCancelled = "turn_cancelled";
    public const string TurnInputRequired = "turn_input_required";
}

public sealed class AgentException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public override string ToString() => $"{Code}: {Message}";
}

public sealed record AgentSessionRequest(
    Issue Issue,
    string WorkspacePath,
    ServiceConfig Config,
    ITrackerAdapter Tracker);

public interface IAgentSession : IAsyncDisposable
{
    string? ThreadId { get; }

    /// <summary>Runs one turn to completion. Throws <see cref="AgentException"/> on failure.</summary>
    Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct);
}

public interface IAgentSessionFactory
{
    Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct);
}

/// <summary>Host-side endpoint that serves the tracker adapter's tools to one agent session.</summary>
public sealed record ToolEndpoint(string Url, string BearerToken, IDisposable Registration);

public interface IToolGateway
{
    /// <summary>
    /// Registers a session-scoped tool endpoint bound to one adapter snapshot (SPEC 10.5). The
    /// current issue is re-read on each call so continuation turns see refreshed issue context.
    /// </summary>
    ToolEndpoint Register(ITrackerAdapter tracker, Func<Issue> currentIssue, string issueIdentifier);
}
