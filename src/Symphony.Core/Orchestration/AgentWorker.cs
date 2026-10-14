using System.Globalization;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Templating;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Core.Orchestration;

public sealed class WorkerFailedException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>
/// One worker attempt: workspace + hooks + prompt + multi-turn agent session (SPEC 10.7, 16.5).
/// Normal return means a clean exit; any failure throws.
/// </summary>
public sealed class AgentWorker(WorkspaceManager workspaces, IAgentSessionFactory agents, ILogger<AgentWorker> logger)
{
    public async Task RunAsync(
        Issue issue,
        int? attempt,
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
                && !await workspaces.RunHookAsync("before_run", beforeRun, workspace.Path, issue.Identifier, config.Hooks, ct))
            {
                throw new WorkerFailedException("before_run hook error");
            }

            await RunSessionAsync(issue, attempt, workflow, workspace, onEvent, ct);
        }
        finally
        {
            // after_run runs after success, failure, timeout or cancellation once the workspace exists.
            if (config.Hooks.AfterRun is { } afterRun)
            {
                await workspaces.RunHookAsync("after_run", afterRun, workspace.Path, issue.Identifier, config.Hooks, CancellationToken.None);
            }
        }
    }

    private async Task RunSessionAsync(
        Issue issue,
        int? attempt,
        EffectiveWorkflow workflow,
        Workspace workspace,
        Action<AgentEvent> onEvent,
        CancellationToken ct)
    {
        var config = workflow.Config;
        // Invariant 1: the agent runs only in the per-issue workspace under the root.
        WorkspaceManager.EnsureInsideRoot(config.WorkspaceRoot, workspace.Path);

        IAgentSession session;
        try
        {
            session = await agents.StartAsync(new AgentSessionRequest(issue, workspace.Path, config, workflow.Tracker), onEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkerFailedException($"agent session startup error: {ex.Message}", ex);
        }

        await using var _ = session;
        var maxTurns = config.Agent.MaxTurns;
        for (var turn = 1; ; turn++)
        {
            string prompt;
            try
            {
                prompt = turn == 1
                    ? PromptRenderer.Render(workflow.PromptTemplate, issue, attempt)
                    : ContinuationPrompt(issue, turn, maxTurns);
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

            if (turn >= maxTurns)
            {
                logger.LogInformation("action=worker outcome=stopping issue_id={IssueId} issue_identifier={IssueIdentifier} reason=max_turns max_turns={MaxTurns}", issue.Id, issue.Identifier, maxTurns);
                return;
            }
        }
    }

    /// <summary>Continuation guidance only; the original task prompt is already in thread history (SPEC 7.1).</summary>
    public static string ContinuationPrompt(Issue issue, int turn, int maxTurns) =>
        $"""
        Continue working on {issue.Identifier}: {issue.Title}.
        The work item is still in state "{issue.State}" in the tracker, so the task is not finished yet.
        This is continuation turn {turn} of at most {maxTurns} in this session. Review where you left off,
        finish the remaining work, and move the work item to the workflow's handoff state when done.
        """;
}
