namespace Symphony.Core.Config;

public sealed record TrackerSettings(
    string Kind,
    IReadOnlyDictionary<string, object?> Provider,
    IReadOnlyList<string> RequiredLabels,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates);

public sealed record HookSettings(
    string? AfterCreate,
    string? BeforeRun,
    string? AfterRun,
    string? BeforeRemove,
    int TimeoutMs);

public sealed record AgentSettings(
    int MaxConcurrentAgents,
    int MaxTurns,
    int MaxRetryBackoffMs,
    IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState,
    int MaxFastContinuations);

/// <summary>
/// Claude Code runner settings (front matter key <c>claude</c>). This replaces the SPEC's
/// <c>codex</c> section; see docs/IMPLEMENTATION.md for the mapping.
/// </summary>
public sealed record ClaudeSettings(
    string Command,
    string? Model,
    string PermissionMode,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    IReadOnlyList<string> ExtraArgs,
    bool StrictMcpConfig,
    int TurnTimeoutMs,
    int ReadTimeoutMs,
    int StallTimeoutMs);

public sealed record ServiceConfig(
    string WorkflowPath,
    TrackerSettings Tracker,
    int PollIntervalMs,
    string WorkspaceRoot,
    HookSettings Hooks,
    AgentSettings Agent,
    ClaudeSettings Claude,
    int? ServerPort,
    IReadOnlyList<string> Warnings)
{
    public bool IsActiveState(string state)
    {
        var n = Domain.StateNames.Normalize(state);
        return Tracker.ActiveStates.Any(s => Domain.StateNames.Normalize(s) == n)
            && !IsTerminalState(state);
    }

    public bool IsTerminalState(string state)
    {
        var n = Domain.StateNames.Normalize(state);
        return Tracker.TerminalStates.Any(s => Domain.StateNames.Normalize(s) == n);
    }
}
