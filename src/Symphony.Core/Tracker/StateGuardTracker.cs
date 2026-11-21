using System.Text.Json.Nodes;
using Symphony.Core.Config;
using Symphony.Core.Domain;

namespace Symphony.Core.Tracker;

/// <summary>
/// The tracker as an agent session sees it: <c>update_state</c> only reaches the states the workflow lets an agent
/// move an issue to (<see cref="AgentSettings.MayMoveTo"/>), so an agent can't close an issue past its review
/// handoff and its gate. When the tracker itself rejects a state, the result tells the agent to stop trying
/// others. Every other call goes straight through. Does not own <paramref name="inner"/>. See docs/agent-guardrails.md.
/// </summary>
public sealed class StateGuardTracker(ITrackerAdapter inner, ServiceConfig config) : ITrackerAdapter
{
    public const string RefusedCategory = "state_not_allowed";

    private const string StopTrying =
        " Don't try other states to get around this: leave the state as it is, explain in add_comment what is done"
        + " and which state you tried, and end your turn.";

    public ITrackerAdapter Inner => inner;

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<string> SecretEnvironmentNames() => inner.SecretEnvironmentNames();

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
        inner.AgentToolSpecs().Select(s => s.Name == Gates.GatedTracker.UpdateStateTool ? s with { Description = s.Description + " " + Policy() } : s).ToList();

    private string Policy() => config.Agent.AllowedStates is { } allowed
        ? $"Symphony only lets you move the issue to {Quoted(allowed)}."
        : $"Symphony doesn't let you move the issue to a terminal state ({Quoted(config.Tracker.TerminalStates)}).";

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        if (name != Gates.GatedTracker.UpdateStateTool || arguments["state"] is not JsonValue v || !v.TryGetValue(out string? target))
        {
            return await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        }

        if (!config.Agent.MayMoveTo(target, config.Tracker.TerminalStates))
        {
            return AgentToolResult.Fail(RefusedCategory,
                $"Symphony refused to move {issue.Identifier} to \"{target}\", so the status was not changed. {Policy()} Moving it past"
                + " the handoff state is for a person to do after review." + StopTrying);
        }

        var result = await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        if (!result.Success && result.Output?["error"] is JsonObject error
            && error["category"]?.GetValue<string>() == TrackerErrorCategories.Status
            && error["message"] is JsonValue m && m.TryGetValue(out string? message))
        {
            error["message"] = message + StopTrying;
        }

        return result;
    }

    private static string Quoted(IEnumerable<string> states) => string.Join(", ", states.Select(s => $"\"{s}\""));

    public void Dispose()
    {
        // The tracker belongs to the workflow and outlives the session.
    }
}
