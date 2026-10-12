using System.Text.Json.Nodes;

namespace Symphony.Core.Orchestration;

public sealed record TokenCounts(long InputTokens, long OutputTokens, long TotalTokens);

public sealed record RunningRow(
    string IssueId,
    string IssueIdentifier,
    string? IssueUrl,
    string State,
    string? SessionId,
    int TurnCount,
    string? LastEvent,
    string? LastMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastEventAt,
    TokenCounts Tokens,
    string? WorkspacePath,
    string? Stopping);

public sealed record RetryRow(
    string IssueId,
    string IssueIdentifier,
    string? IssueUrl,
    int Attempt,
    DateTimeOffset DueAt,
    string? Error);

public sealed record AgentTotals(long InputTokens, long OutputTokens, long TotalTokens, double SecondsRunning);

public sealed record RecentEvent(DateTimeOffset At, string Event, string? Message);

public sealed record IssueDetail(
    string IssueIdentifier,
    string IssueId,
    string Status,
    string? WorkspacePath,
    int RestartCount,
    int? CurrentRetryAttempt,
    RunningRow? Running,
    RetryRow? Retry,
    IReadOnlyList<RecentEvent> RecentEvents,
    string? LastError);

/// <summary>Synchronous runtime snapshot (SPEC 13.3).</summary>
public sealed record RuntimeSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RunningRow> Running,
    IReadOnlyList<RetryRow> Retrying,
    AgentTotals Totals,
    JsonNode? RateLimits,
    string? WorkflowError,
    IReadOnlyDictionary<string, IssueDetail> Issues);

public sealed record RefreshResult(bool Queued, bool Coalesced, DateTimeOffset RequestedAt);
