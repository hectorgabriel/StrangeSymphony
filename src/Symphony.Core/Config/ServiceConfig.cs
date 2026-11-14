using Symphony.Core.Domain;

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
    int TimeoutMs,
    // auto | bash | powershell | pwsh | absolute path to a bash; see ShellRunner.Resolve.
    string Shell = Util.ShellRunner.Auto);

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
    // Extension: normalized state name -> model, overriding Model for issues in that state (docs/hybrid-models.md).
    IReadOnlyDictionary<string, string> ModelByState,
    string PermissionMode,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    IReadOnlyList<string> ExtraArgs,
    bool StrictMcpConfig,
    int TurnTimeoutMs,
    int ReadTimeoutMs,
    int StallTimeoutMs,
    // Extension: continue the issue's last session in the same state instead of starting a new one.
    bool Resume = true)
{
    /// <summary>The model a session for an issue in <paramref name="state"/> runs with; null means Claude Code's default.</summary>
    public string? ModelFor(string? state) =>
        ModelByState.TryGetValue(StateNames.Normalize(state), out var model) ? model : Model;
}

/// <summary>
/// Front matter key <c>pull_request</c> (extension): lets the agent open a pull request through the
/// host, which first checks the branch and runs <see cref="VerifyCommand"/>. See docs/pull-requests.md.
/// </summary>
public sealed record PullRequestSettings(
    string Kind,
    IReadOnlyDictionary<string, object?> Provider,
    string Remote,
    string TargetBranch,
    bool Draft,
    string VerifyCommand,
    int VerifyTimeoutMs,
    int MaxFiles,
    IReadOnlyList<string> ForbiddenPaths);

/// <summary>
/// Front matter key <c>budget</c> (extension): token caps that pause work instead of letting a looping
/// agent drain the plan. Tokens are counted as the dashboard shows them; 0 means no limit.
/// </summary>
public sealed record BudgetSettings(long MaxTokensPerIssue, long MaxTokensPerDay)
{
    public static readonly BudgetSettings None = new(0, 0);
}

public sealed record ServiceConfig(
    string WorkflowPath,
    TrackerSettings Tracker,
    int PollIntervalMs,
    string WorkspaceRoot,
    HookSettings Hooks,
    AgentSettings Agent,
    ClaudeSettings Claude,
    int? ServerPort,
    IReadOnlyList<string> Warnings,
    PullRequestSettings? PullRequest = null,
    BudgetSettings? Budget = null)
{
    public BudgetSettings Budgets => Budget ?? BudgetSettings.None;

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
