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

/// <summary>
/// An active-state candidate from the last poll and why it is or isn't running. <see cref="Status"/> is one of
/// running, retrying, claimed, ready, waiting, blocked, held, missing_labels, invalid.
/// </summary>
public sealed record CandidateRow(
    string IssueId,
    string IssueIdentifier,
    string? IssueUrl,
    string Title,
    string State,
    int? Priority,
    string Status,
    string Reason);

/// <summary>
/// An issue Symphony recently let go of, newest first. <see cref="State"/> is the tracker's current state,
/// re-read on every poll until the issue reaches a terminal state (<see cref="Closed"/>); null when it left
/// the tracker's scope. <see cref="HandoffState"/> and <see cref="Reason"/> describe the release itself.
/// </summary>
public sealed record HandoffRow(
    string IssueId,
    string IssueIdentifier,
    string? IssueUrl,
    string? Title,
    string? State,
    DateTimeOffset At,
    string Reason,
    string? HandoffState = null,
    bool Closed = false,
    DateTimeOffset? CheckedAt = null);

/// <summary>The last candidate poll. Only issues in <see cref="ActiveStates"/> are fetched, so only they can appear in the queue.</summary>
public sealed record PollInfo(DateTimeOffset? At, string? Error, IReadOnlyList<string> ActiveStates);

/// <summary>Synchronous runtime snapshot (SPEC 13.3).</summary>
public sealed record RuntimeSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RunningRow> Running,
    IReadOnlyList<RetryRow> Retrying,
    AgentTotals Totals,
    JsonNode? RateLimits,
    string? WorkflowError,
    IReadOnlyDictionary<string, IssueDetail> Issues,
    IReadOnlyList<CandidateRow> Queue,
    PollInfo LastPoll,
    IReadOnlyList<HandoffRow> Handoffs);

public sealed record RefreshResult(bool Queued, bool Coalesced, DateTimeOffset RequestedAt);
