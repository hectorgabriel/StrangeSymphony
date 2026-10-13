using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Core.Domain;
using Symphony.Core.Util;

namespace Symphony.AzureDevOps;

/// <summary>Maps Azure DevOps work item payloads to the normalized <see cref="Issue"/> (SPEC 11.3).</summary>
public static class WorkItemNormalizer
{
    public const string PredecessorRel = "System.LinkTypes.Dependency-Reverse";

    public sealed record Raw(
        int Id,
        string? Title,
        string? State,
        string? TeamProject,
        string? WorkItemType,
        string? AreaPath,
        JsonObject Fields,
        IReadOnlyList<int> PredecessorIds,
        int? Rev);

    public static Raw? ReadRaw(JsonObject workItem)
    {
        if (workItem["id"] is not JsonValue idValue || !idValue.TryGetValue(out int id))
        {
            return null;
        }

        var fields = workItem["fields"] as JsonObject ?? new JsonObject();
        var predecessors = new List<int>();
        if (workItem["relations"] is JsonArray relations)
        {
            foreach (var rel in relations.OfType<JsonObject>())
            {
                if (Str(rel["rel"]) == PredecessorRel && TryIdFromUrl(Str(rel["url"]), out var pid))
                {
                    predecessors.Add(pid);
                }
            }
        }

        int? rev = workItem["rev"] is JsonValue rv && rv.TryGetValue(out int r) ? r : null;
        return new Raw(id, Str(fields["System.Title"]), Str(fields["System.State"]), Str(fields["System.TeamProject"]),
            Str(fields["System.WorkItemType"]), Str(fields["System.AreaPath"]), fields, predecessors, rev);
    }

    /// <summary>
    /// Builds the normalized issue, or returns null when required fields are missing (malformed).
    /// <paramref name="dispatchable"/> is computed by the adapter.
    /// </summary>
    public static Issue? ToIssue(Raw raw, AzureDevOpsOptions options, IReadOnlyList<BlockerRef> blockers, bool dispatchable)
    {
        if (string.IsNullOrWhiteSpace(raw.Title) || string.IsNullOrWhiteSpace(raw.State))
        {
            return null;
        }

        var f = raw.Fields;
        var assigned = f["System.AssignedTo"] as JsonObject;
        var nativeRef = new JsonObject
        {
            ["organization_url"] = options.OrganizationUrl.ToString().TrimEnd('/'),
            ["project"] = raw.TeamProject ?? options.Project,
            ["work_item_id"] = raw.Id,
            ["work_item_type"] = raw.WorkItemType,
            ["rev"] = raw.Rev,
            ["area_path"] = raw.AreaPath,
            ["iteration_path"] = Str(f["System.IterationPath"]),
            ["assigned_to"] = Str(assigned?["uniqueName"]),
        };

        return new Issue
        {
            Id = raw.Id.ToString(CultureInfo.InvariantCulture),
            NativeRef = nativeRef,
            Identifier = Identifier(options, raw.Id),
            Title = raw.Title.Trim(),
            Description = Description(f),
            Priority = Priority(f["Microsoft.VSTS.Common.Priority"]),
            State = raw.State,
            BranchName = null,
            Url = $"{options.OrganizationUrl.ToString().TrimEnd('/')}/{Uri.EscapeDataString(raw.TeamProject ?? options.Project)}/_workitems/edit/{raw.Id}",
            AssigneeId = Str(assigned?["id"]),
            Labels = Labels(Str(f["System.Tags"])),
            BlockedBy = blockers,
            Dispatchable = dispatchable,
            CreatedAt = Timestamp(f["System.CreatedDate"]),
            UpdatedAt = Timestamp(f["System.ChangedDate"]),
        };
    }

    public static string Identifier(AzureDevOpsOptions options, int id) => $"{options.IdentifierPrefix}-{id}";

    public static bool AssigneeMatches(JsonObject fields, string? expectedId, string? expectedName)
    {
        if (fields["System.AssignedTo"] is not JsonObject assigned)
        {
            return false;
        }

        if (expectedId is not null && string.Equals(Str(assigned["id"]), expectedId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return expectedName is not null
            && (string.Equals(Str(assigned["uniqueName"]), expectedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Str(assigned["displayName"]), expectedName, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> Labels(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

    public static string? Description(JsonObject f)
    {
        var parts = new List<string>();
        if (Html.ToText(Str(f["System.Description"])) is { Length: > 0 } d) parts.Add(d);
        if (Html.ToText(Str(f["Microsoft.VSTS.TCM.ReproSteps"])) is { Length: > 0 } r) parts.Add($"Repro steps:\n{r}");
        if (Html.ToText(Str(f["Microsoft.VSTS.Common.AcceptanceCriteria"])) is { Length: > 0 } a) parts.Add($"Acceptance criteria:\n{a}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static int? Priority(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out int i) => i,
        JsonValue v when v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    private static DateTimeOffset? Timestamp(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static bool TryIdFromUrl(string? url, out int id)
    {
        id = 0;
        if (url is null) return false;
        var last = url.TrimEnd('/').Split('/').LastOrDefault();
        return int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
