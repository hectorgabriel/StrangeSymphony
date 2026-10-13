using System.Text.Json.Nodes;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Wrike;

/// <summary>
/// Custom status names and ids from <c>GET /workflows</c>. Names are resolved inside the queue's workflow;
/// ids from any workflow can be named, so tasks moved to another workflow still show a state.
/// </summary>
public sealed class WrikeStatusMap
{
    public sealed record Status(string Id, string Name, string Group, bool Hidden);

    private readonly Dictionary<string, Status> _byId;
    private readonly List<Status> _workflow;

    private WrikeStatusMap(string workflowId, string workflowName, Dictionary<string, Status> byId, List<Status> workflow)
    {
        WorkflowId = workflowId;
        WorkflowName = workflowName;
        _byId = byId;
        _workflow = workflow;
    }

    public string WorkflowId { get; }

    public string WorkflowName { get; }

    /// <summary>Visible status names of the queue's workflow, in Wrike's order.</summary>
    public IReadOnlyList<string> VisibleNames => _workflow.Where(s => !s.Hidden).Select(s => s.Name).Distinct().ToList();

    /// <summary>
    /// Picks the workflow named (or with the id) <paramref name="selector"/>, otherwise the one that owns
    /// <paramref name="referenceStatusId"/> (the queue task's own status).
    /// </summary>
    public static WrikeStatusMap Build(IReadOnlyList<JsonObject> workflows, string? selector, string? referenceStatusId)
    {
        var byId = new Dictionary<string, Status>(StringComparer.Ordinal);
        var parsed = new List<(JsonObject Workflow, List<Status> Statuses)>();
        foreach (var workflow in workflows)
        {
            var statuses = (workflow["customStatuses"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(s => WrikeNormalizer.Str(s["id"]) is { } id && WrikeNormalizer.Str(s["name"]) is { } name
                    ? new Status(id, name, WrikeNormalizer.Str(s["group"]) ?? "", s["hidden"]?.GetValue<bool>() == true)
                    : null)
                .OfType<Status>()
                .ToList();
            foreach (var status in statuses)
            {
                byId[status.Id] = status;
            }

            parsed.Add((workflow, statuses));
        }

        List<(JsonObject Workflow, List<Status> Statuses)> matches;
        if (selector is not null)
        {
            matches = parsed.Where(p => string.Equals(WrikeNormalizer.Str(p.Workflow["id"]), selector, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
            {
                var named = parsed.Where(p => StateNames.Normalize(WrikeNormalizer.Str(p.Workflow["name"])) == StateNames.Normalize(selector)).ToList();
                // Wrike keeps old copies of a workflow as hidden ones with the same name.
                matches = named.Count > 1 ? named.Where(p => p.Workflow["hidden"]?.GetValue<bool>() != true).ToList() : named;
            }
        }
        else
        {
            matches = referenceStatusId is null ? [] : parsed.Where(p => p.Statuses.Any(s => s.Id == referenceStatusId)).ToList();
        }

        if (matches.Count != 1)
        {
            throw new TrackerException(TrackerErrorCategories.InvalidTrackerConfig, selector is null
                ? "could not find the queue task's workflow; set tracker.provider.workflow"
                : $"tracker.provider.workflow '{selector}' matches {matches.Count} Wrike workflows; use its id instead");
        }

        return new WrikeStatusMap(WrikeNormalizer.Str(matches[0].Workflow["id"]) ?? "", WrikeNormalizer.Str(matches[0].Workflow["name"]) ?? "",
            byId, matches[0].Statuses);
    }

    /// <summary>True when some status of the queue's workflow has this name, hidden or not.</summary>
    public bool Exists(string name) => _workflow.Any(s => StateNames.Normalize(s.Name) == StateNames.Normalize(name));

    public Status? ById(string? id) => id is not null && _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Resolves a status name in the queue's workflow. Wrike allows repeated names, so a visible status wins over
    /// hidden ones; a name that still matches several statuses is rejected rather than guessed.
    /// </summary>
    public bool TryResolve(string name, out Status? status, out string? error)
    {
        var n = StateNames.Normalize(name);
        var all = _workflow.Where(s => StateNames.Normalize(s.Name) == n).ToList();
        var visible = all.Where(s => !s.Hidden).ToList();
        var pick = visible.Count > 0 ? visible : all;
        if (pick.Count == 1)
        {
            status = pick[0];
            error = null;
            return true;
        }

        status = null;
        error = pick.Count == 0
            ? $"'{name}' is not a status of the Wrike workflow '{WorkflowName}'; valid statuses: {string.Join(", ", VisibleNames)}"
            : $"'{name}' matches {pick.Count} statuses of the Wrike workflow '{WorkflowName}' ({string.Join(", ", pick.Select(s => s.Group))})";
        return false;
    }
}
