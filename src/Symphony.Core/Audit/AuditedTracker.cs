using System.Text.Json.Nodes;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Core.Audit;

/// <summary>
/// The tracker as an agent session sees it, reporting every host tool call for the audit trail: which tool, the
/// summarized arguments, whether it worked, and for <c>update_state</c> the move, with the gate's commit when a
/// verification gate passed. It is the outermost wrapper, so a call a gate refused is reported as refused.
/// Does not own <paramref name="inner"/>.
/// </summary>
public sealed class AuditedTracker(ITrackerAdapter inner, Action<AgentEvent> onEvent) : ITrackerAdapter
{
    /// <summary>The tracker this one reports for.</summary>
    public ITrackerAdapter Inner => inner;

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<string> SecretEnvironmentNames() => inner.SecretEnvironmentNames();

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() => inner.AgentToolSpecs();

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        var args = AuditLog.SummarizeArgs(arguments);
        AgentToolResult result;
        try
        {
            result = await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(name, args, issue, ok: false, category: "exception", output: null);
            throw;
        }

        var category = result.Success ? null : result.Output?["error"]?["category"]?.GetValue<string>() ?? "failed";
        if (name == Defects.DefectTracker.ReportTool && result.Success && result.Output is JsonObject counted)
        {
            // Only fixed category and severity names, so the counts are kept instead of fingerprints.
            args = new JsonObject { ["categories"] = counted["categories"]?.DeepClone(), ["severities"] = counted["severities"]?.DeepClone() };
        }

        Report(name, args, issue, result.Success, category, result.Output as JsonObject);
        return result;
    }

    private void Report(string name, JsonObject args, Issue issue, bool ok, string? category, JsonObject? output)
    {
        var state = args["state"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        var gate = output?["gate"] as JsonObject;
        var call = new HostToolCall(
            name, args, ok, category, state,
            FromState: state is null ? null : issue.State,
            Commit: Str(gate?["commit"]),
            Branch: Str(gate?["branch"]),
            Url: Str(output?["url"]));
        onEvent(new AgentEvent(AgentEventNames.HostToolCall, DateTimeOffset.UtcNow) { HostTool = call });
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public void Dispose()
    {
        // The tracker belongs to the workflow and outlives the session.
    }
}
