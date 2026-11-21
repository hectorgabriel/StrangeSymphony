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
    int MaxFastContinuations,
    // Extension, agent.runner: the coding agent sessions run with (Agents.AgentRunners).
    string Runner = Agents.AgentRunners.Claude,
    // Extension, agent.runner_by_state: normalized state name -> runner, overriding Runner in that state.
    IReadOnlyDictionary<string, string>? RunnerByState = null)
{
    /// <summary>The runner a session for an issue in <paramref name="state"/> runs with.</summary>
    public string RunnerFor(string? state) =>
        RunnerByState is not null && RunnerByState.TryGetValue(StateNames.Normalize(state), out var runner) ? runner : Runner;

    /// <summary>The default runner and every runner a state is routed to.</summary>
    public IReadOnlyList<string> RunnersInUse =>
        [.. new[] { Runner }.Concat(RunnerByState?.Values ?? []).Distinct(StringComparer.Ordinal)];
}

/// <summary>What the orchestrator needs from any runner's section, whichever agent it configures.</summary>
public interface IRunnerSettings
{
    string Command { get; }

    /// <summary>The model a session for an issue in <paramref name="state"/> runs with; null means the agent's default.</summary>
    string? ModelFor(string? state);

    int TurnTimeoutMs { get; }

    /// <summary>No agent activity for this long stops the session as stalled; 0 or less turns detection off.</summary>
    int StallTimeoutMs { get; }

    /// <summary>Continue the issue's last session in the same state instead of starting a new one (the section's <c>resume</c>).</summary>
    bool ResumeSessions { get; }
}

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
    bool Resume = true) : IRunnerSettings
{
    /// <summary>The model a session for an issue in <paramref name="state"/> runs with; null means Claude Code's default.</summary>
    public string? ModelFor(string? state) =>
        ModelByState.TryGetValue(StateNames.Normalize(state), out var model) ? model : Model;

    bool IRunnerSettings.ResumeSessions => Resume;
}

/// <summary>
/// GitHub Copilot CLI runner settings (front matter key <c>copilot</c>, used when <c>agent.runner</c> is
/// <c>copilot</c>). See docs/copilot.md.
/// </summary>
public sealed record CopilotSettings(
    string Command,
    string? Model,
    // Normalized state name -> model, overriding Model for issues in that state.
    IReadOnlyDictionary<string, string> ModelByState,
    // none | minimal | low | medium | high | xhigh | max; null means the model's default.
    string? ReasoningEffort,
    // --allow-tool rules (shell(git:*), write, url(...)); a -p run denies anything else that would prompt.
    IReadOnlyList<string> AllowTools,
    // --deny-tool rules; they win over every allow.
    IReadOnlyList<string> DenyTools,
    // --excluded-tools: tools the model never sees.
    IReadOnlyList<string> ExcludedTools,
    // COPILOT_HOME for agent sessions; null means a private directory under Symphony's state directory.
    string? Home,
    // Add workspace.root to the home's trustedFolders, so repository hooks (.claude/settings.json) and .mcp.json run.
    bool TrustWorkspaceRoot,
    IReadOnlyList<string> ExtraArgs,
    int TurnTimeoutMs,
    int StallTimeoutMs,
    bool Resume = true) : IRunnerSettings
{
    public static readonly IReadOnlyList<string> ReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public string? ModelFor(string? state) =>
        ModelByState.TryGetValue(StateNames.Normalize(state), out var model) ? model : Model;

    bool IRunnerSettings.ResumeSessions => Resume;
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

/// <summary>
/// One entry of the front matter key <c>gates</c> (extension; docs/verification-gates.md): checks the host
/// runs in the workspace before the agent may move an issue to <see cref="State"/>.
/// </summary>
public sealed record GateSettings(
    string State,
    IReadOnlyList<string> Run,
    bool RequirePushed,
    string Remote,
    int TimeoutMs);

/// <summary>
/// The front matter key <c>defects</c> (extension; docs/rework-loops.md): the statuses that send work back
/// (<see cref="States"/>, normalized) and the categories an agent reports defects in before it moves an issue there.
/// <see cref="Categories"/> maps each category name to its description, in workflow order.
/// </summary>
public sealed record DefectSettings(IReadOnlyList<string> States, IReadOnlyList<KeyValuePair<string, string>> Categories)
{
    public static readonly IReadOnlyList<string> Severities = ["critical", "major", "minor"];

    public bool IsDefectState(string? state) => States.Contains(Domain.StateNames.Normalize(state));
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
    BudgetSettings? Budget = null,
    // Normalized target state name -> gate.
    IReadOnlyDictionary<string, GateSettings>? Gates = null,
    // Extension, workspace.strip_env: variable names or patterns removed from every process Symphony starts in a
    // workspace (agent, hooks, gate commands, verify_command). See docs/data-isolation.md.
    IReadOnlyList<string>? StripEnv = null,
    // Extension, audit.path: where the audit trail goes; null means <workspace.root>/.symphony-audit.jsonl.
    string? AuditPath = null,
    // Extension, defects: send-back statuses and defect categories; null means no report_defects tool.
    DefectSettings? Defects = null,
    // Extension, copilot: the GitHub Copilot CLI runner (agent.runner: copilot).
    CopilotSettings? Copilot = null)
{
    public BudgetSettings Budgets => Budget ?? BudgetSettings.None;

    /// <summary>The section of the default runner (<c>agent.runner</c>).</summary>
    public IRunnerSettings RunnerSettings => SettingsFor(Agent.Runner);

    /// <summary>The section that configures <paramref name="runner"/>.</summary>
    public IRunnerSettings SettingsFor(string runner) => runner switch
    {
        Agents.AgentRunners.Claude => Claude,
        Agents.AgentRunners.Copilot => Copilot ?? throw new InvalidOperationException("the copilot section was not built"),
        _ => throw new InvalidOperationException($"agent runner '{runner}' is not supported"),
    };

    /// <summary>
    /// The runner and model a session for an issue in <paramref name="state"/> starts with: the state's runner
    /// (<c>agent.runner_by_state</c>, else <c>agent.runner</c>), and that runner's model for the state.
    /// </summary>
    public Agents.AgentRoute RouteFor(string? state)
    {
        var runner = Agent.RunnerFor(state);
        return new(runner, SettingsFor(runner).ModelFor(state));
    }

    /// <summary>The audit trail's file (docs/audit-trail.md).</summary>
    public string AuditFile => AuditPath ?? Audit.AuditLog.DefaultPathFor(WorkspaceRoot);

    public IReadOnlyList<string> StrippedEnvironment => StripEnv ?? [];

    /// <summary>The gate for moving an issue to <paramref name="state"/>, if the workflow sets one.</summary>
    public GateSettings? GateFor(string? state) =>
        Gates is not null && Gates.TryGetValue(Domain.StateNames.Normalize(state), out var gate) ? gate : null;

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
