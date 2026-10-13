using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Core.Domain;

namespace Symphony.Wrike;

/// <summary>Maps Wrike task payloads to the normalized <see cref="Issue"/> (SPEC 11.3).</summary>
public static class WrikeNormalizer
{
    /// <summary>
    /// Builds the normalized issue, or returns null when required fields are missing (malformed).
    /// <paramref name="state"/> is the custom status name; <paramref name="dispatchable"/> is computed by the adapter.
    /// </summary>
    public static Issue? ToIssue(JsonObject task, string? state, string? description, WrikeOptions options, string parentTaskId,
        IReadOnlyList<BlockerRef> blockers, bool dispatchable)
    {
        var id = Str(task["id"]);
        var title = Str(task["title"]);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var permalink = Str(task["permalink"]);
        return new Issue
        {
            Id = id,
            NativeRef = new JsonObject
            {
                ["task_id"] = id,
                ["permalink"] = permalink,
                ["parent_task_id"] = parentTaskId,
                ["custom_status_id"] = Str(task["customStatusId"]),
                ["status_group"] = Str(task["status"]),
                ["importance"] = Str(task["importance"]),
            },
            Identifier = Identifier(options, task),
            Title = title.Trim(),
            Description = description,
            Priority = Priority(Str(task["importance"])),
            State = state,
            BranchName = null,
            Url = permalink,
            AssigneeId = Strings(task["responsibleIds"]) is [var first, ..] ? first : null,
            Labels = [],
            BlockedBy = blockers,
            Dispatchable = dispatchable,
            CreatedAt = Timestamp(task["createdDate"]),
            UpdatedAt = Timestamp(task["updatedDate"]),
        };
    }

    /// <summary><c>&lt;prefix&gt;-&lt;permalink id&gt;</c>, the number people see in Wrike; the v4 id when there is no permalink.</summary>
    public static string Identifier(WrikeOptions options, JsonObject task) =>
        $"{options.IdentifierPrefix}-{WrikeOptions.PermalinkId(Str(task["permalink"])) ?? Str(task["id"])}";

    /// <summary>Wrike importance: High, Normal, Low.</summary>
    public static int? Priority(string? importance) => importance switch
    {
        "High" => 1,
        "Normal" => 2,
        "Low" => 3,
        _ => null,
    };

    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().ToList() : [];

    private static DateTimeOffset? Timestamp(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
