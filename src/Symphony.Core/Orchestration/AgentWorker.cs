using System.Globalization;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Gates;
using Symphony.Core.Templating;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Core.Orchestration;

public sealed class WorkerFailedException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>
/// One worker attempt: workspace + hooks + prompt + multi-turn agent session (SPEC 10.7, 16.5).
/// Normal return means a clean exit; any failure throws.
/// </summary>
public sealed class AgentWorker(
    WorkspaceManager workspaces,
    IAgentSessionFactory agents,
    ILogger<AgentWorker> logger,
    IWorkspaceToolsFactory? workspaceTools = null)
{
    public Task RunAsync(
        Issue issue,
        int? attempt,
        EffectiveWorkflow workflow,
        Action<AgentEvent> onEvent,
        Action<string> onWorkspace,
        CancellationToken ct) =>
        RunAsync(issue, attempt, null, workflow, onEvent, onWorkspace, ct);

    /// <param name="resumeSessionId">The issue's earlier session to continue; ignored when the workspace is new.</param>
    public async Task RunAsync(
        Issue issue,
        int? attempt,
        string? resumeSessionId,
        EffectiveWorkflow workflow,
        Action<AgentEvent> onEvent,
        Action<string> onWorkspace,
        CancellationToken ct)
    {
        var config = workflow.Config;
        Workspace workspace;
        try
        {
            workspace = await workspaces.CreateForIssueAsync(issue.Identifier, config, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkerFailedException($"workspace error: {ex.Message}", ex);
        }

        onWorkspace(workspace.Path);

        try
        {
            if (config.Hooks.BeforeRun is { } beforeRun
                && !await workspaces.RunHookAsync("before_run", beforeRun, workspace.Path, issue.Identifier, config.Hooks, ct, config.StrippedEnvironment))
            {
                throw new WorkerFailedException("before_run hook error");
            }

            // A session belongs to the workspace it ran in; a new workspace has nothing to continue.
            await RunSessionAsync(issue, attempt, workspace.CreatedNow ? null : resumeSessionId, workflow, workspace, onEvent, ct);
        }
        finally
        {
            // after_run runs after success, failure, timeout or cancellation once the workspace exists.
            if (config.Hooks.AfterRun is { } afterRun)
            {
                await workspaces.RunHookAsync("after_run", afterRun, workspace.Path, issue.Identifier, config.Hooks, CancellationToken.None, config.StrippedEnvironment);
            }
        }
    }

    private async Task RunSessionAsync(
        Issue issue,
        int? attempt,
        string? resume,
        EffectiveWorkflow workflow,
        Workspace workspace,
        Action<AgentEvent> onEvent,
        CancellationToken ct)
    {
        var config = workflow.Config;
        // Invariant 1: the agent runs only in the per-issue workspace under the root.
        WorkspaceManager.EnsureInsideRoot(config.WorkspaceRoot, workspace.Path);

        // Workspace tools are bound to this workspace, so they live exactly as long as the session.
        ToolAugmentedTracker? tools;
        try
        {
            // Code the agent wrote must see neither the tracker's credentials nor what the workflow strips.
            IReadOnlyList<string> hostSecrets = [.. workflow.Tracker.SecretEnvironmentNames(), .. config.StrippedEnvironment];
            tools = workspaceTools?.Create(config, workspace.Path, issue, hostSecrets) is { } extra
                ? new ToolAugmentedTracker(workflow.Tracker, extra)
                : null;
        }
        catch (Exception ex)
        {
            throw new WorkerFailedException($"workspace tools error: {ex.Message}", ex);
        }

        using var toolsScope = tools;
        var sessionTracker = (ITrackerAdapter?)tools ?? workflow.Tracker;
        if (config.Gates is { Count: > 0 } gates)
        {
            // State changes go through the host's verification gates; see docs/verification-gates.md.
            sessionTracker = new GatedTracker(sessionTracker, gates, workspace.Path, config.Hooks.Shell, logger, onEvent, config.StrippedEnvironment);
        }

        if (config.Defects is { } defects)
        {
            // Outside the gates, so a missing defect report is refused before a gate runs; see docs/rework-loops.md.
            sessionTracker = new Defects.DefectTracker(sessionTracker, defects, onEvent);
        }

        // Outside the gates and defects, so a state the agent may not reach is refused before anything runs.
        sessionTracker = new StateGuardTracker(sessionTracker, config);

        // Outside the guard, so the audit trail sees what the agent got back, including a refusal.
        sessionTracker = new Audit.AuditedTracker(sessionTracker, onEvent);
        var progress = new ProgressCountingTracker(sessionTracker);
        sessionTracker = progress;

        // Same route as the orchestrator's dispatch: both read it from this workflow for the dispatch state.
        var route = config.RouteFor(issue.State);

        async Task<IAgentSession> StartAsync(string? resumeId)
        {
            try
            {
                return await agents.StartAsync(new AgentSessionRequest(issue, workspace.Path, config, sessionTracker, resumeId, route), onEvent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new WorkerFailedException($"agent session startup error: {ex.Message}", ex);
            }
        }

        IAgentSession? session = await StartAsync(resume);
        try
        {
            var maxTurns = config.Agent.MaxTurns;
            var maxIdle = config.Agent.MaxIdleTurns;
            var idleTurns = 0;
            var workspaceBefore = maxIdle > 0 ? await ProgressProbe.WorkspaceAsync(workspace.Path, ct) : null;
            var changesBefore = 0;
            for (var turn = 1; ; turn++)
            {
                string prompt;
                try
                {
                    prompt = turn > 1 ? ContinuationPrompt(issue, turn, maxTurns)
                        : resume is not null ? ResumePrompt(issue)
                        : PromptRenderer.Render(workflow.PromptTemplate, issue, attempt);
                }
                catch (Exception ex)
                {
                    throw new WorkerFailedException($"prompt error: {ex.Message}", ex);
                }

                onEvent(new AgentEvent(AgentEventNames.TurnStarted, DateTimeOffset.UtcNow) { TurnId = turn.ToString(CultureInfo.InvariantCulture), Message = $"turn {turn}/{maxTurns}" });
                try
                {
                    await session.RunTurnAsync(prompt, issue, ct);
                }
                catch (AgentException ex) when (turn == 1 && resume is not null && !ct.IsCancellationRequested
                    && ex.Code is AgentErrorCodes.PortExit or AgentErrorCodes.TurnFailed)
                {
                    // The saved session is gone or unusable (deleted, another machine, too long): start over once.
                    logger.LogWarning("action=worker outcome=resume_failed issue_id={IssueId} issue_identifier={IssueIdentifier} session_id={SessionId} reason={Reason} note=starting_new_session",
                        issue.Id, issue.Identifier, resume, ex.Message);
                    onEvent(new AgentEvent(AgentEventNames.Notification, DateTimeOffset.UtcNow) { Message = $"could not resume session {resume}; starting a new one" });
                    var old = session;
                    session = null;
                    await old.DisposeAsync();
                    resume = null;
                    session = await StartAsync(null);
                    turn = 0;
                    continue;
                }
                catch (AgentException ex)
                {
                    throw new WorkerFailedException($"agent turn error: {ex.Code}: {ex.Message}", ex);
                }

                IReadOnlyList<Issue> refreshed;
                try
                {
                    refreshed = await workflow.Tracker.FetchIssuesByIdsAsync([issue.Id], ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new WorkerFailedException($"issue state refresh error: {ex.Message}", ex);
                }

                if (refreshed.Count == 0)
                {
                    logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=issue_not_visible", issue.Id, issue.Identifier);
                    return;
                }

                issue = refreshed[0];
                if (!config.IsActiveState(issue.State) || !Eligibility.IsRoutable(issue, config))
                {
                    logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=no_longer_active state={State}", issue.Id, issue.Identifier, issue.State);
                    return;
                }

                // A session keeps its runner and model, so a move to a state routed elsewhere ends it; the
                // continuation dispatch then starts a fresh session with that state's route and prompt.
                if (config.RouteFor(issue.State) != route)
                {
                    logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=route_changed state={State}", issue.Id, issue.Identifier, issue.State);
                    return;
                }

                if (maxIdle > 0)
                {
                    var workspaceAfter = await ProgressProbe.WorkspaceAsync(workspace.Path, ct);
                    var moved = workspaceAfter is null || workspaceAfter != workspaceBefore || progress.Changes != changesBefore;
                    idleTurns = moved ? 0 : idleTurns + 1;
                    workspaceBefore = workspaceAfter;
                    changesBefore = progress.Changes;
                    if (idleTurns >= maxIdle)
                    {
                        logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=idle idle_turns={IdleTurns}", issue.Id, issue.Identifier, idleTurns);
                        onEvent(new AgentEvent(AgentEventNames.SessionIdle, DateTimeOffset.UtcNow)
                        {
                            Message = $"no progress in {idleTurns} turn(s): nothing changed in the workspace or the tracker",
                        });
                        return;
                    }
                }

                if (turn >= maxTurns)
                {
                    logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=max_turns max_turns={MaxTurns}", issue.Id, issue.Identifier, maxTurns);
                    return;
                }
            }
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>First turn of a resumed session: the task prompt and the work so far are already in its history.</summary>
    public static string ResumePrompt(Issue issue) =>
        $"""
        Symphony resumed this session for {issue.Identifier}: {issue.Title}.
        The previous run ended before the work item left state "{issue.State}" (it was interrupted, failed or ran
        out of turns), so the task is not finished. Your instructions and progress are earlier in this conversation.
        Check the workspace first, since the last step may be incomplete, and read the work item's latest comments:
        someone may have answered a question you asked. Then finish the remaining work and move the work item to the
        workflow's handoff state when done.
        """;

    /// <summary>Continuation guidance only; the original task prompt is already in thread history (SPEC 7.1).</summary>
    public static string ContinuationPrompt(Issue issue, int turn, int maxTurns) =>
        $"""
        Continue working on {issue.Identifier}: {issue.Title}.
        The work item is still in state "{issue.State}" in the tracker, so the task is not finished yet.
        This is continuation turn {turn} of at most {maxTurns} in this session. Review where you left off,
        finish the remaining work, and move the work item to the workflow's handoff state when done.
        """;
}
