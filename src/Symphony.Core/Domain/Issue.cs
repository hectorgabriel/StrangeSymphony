using System.Text.Json.Nodes;

namespace Symphony.Core.Domain;

/// <summary>Best-effort blocker reference (SPEC 4.1.1).</summary>
public sealed record BlockerRef(string? Id, string? Identifier, string? State);

/// <summary>
/// Normalized schedulable work item (SPEC 4.1.1). Adapters produce it; the orchestrator treats
/// <see cref="Id"/> and <see cref="NativeRef"/> as opaque.
/// </summary>
public sealed record Issue
{
    public required string Id { get; init; }
    public JsonObject? NativeRef { get; init; }
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public int? Priority { get; init; }
    public required string State { get; init; }
    public string? BranchName { get; init; }
    public string? Url { get; init; }
    public string? AssigneeId { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<BlockerRef> BlockedBy { get; init; } = [];
    public required bool Dispatchable { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Template/tool view with string keys and nested lists preserved (SPEC 12.2).</summary>
    public Dictionary<string, object?> ToTemplateModel() => new()
    {
        ["id"] = Id,
        ["native_ref"] = NativeRef is null ? null : JsonToPlain(NativeRef),
        ["identifier"] = Identifier,
        ["title"] = Title,
        ["description"] = Description,
        ["priority"] = Priority,
        ["state"] = State,
        ["branch_name"] = BranchName,
        ["url"] = Url,
        ["assignee_id"] = AssigneeId,
        ["labels"] = Labels.ToList(),
        ["blocked_by"] = BlockedBy.Select(b => new Dictionary<string, object?>
        {
            ["id"] = b.Id,
            ["identifier"] = b.Identifier,
            ["state"] = b.State,
        }).ToList(),
        ["dispatchable"] = Dispatchable,
        ["created_at"] = CreatedAt?.ToString("O"),
        ["updated_at"] = UpdatedAt?.ToString("O"),
    };

    private static object? JsonToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => JsonToPlain(kv.Value)),
        JsonArray a => a.Select(JsonToPlain).ToList(),
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out long l) => l,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out bool b) => b,
        _ => node.ToJsonString(),
    };
}

public static class StateNames
{
    /// <summary>SPEC 4.2: trim + lowercase.</summary>
    public static string Normalize(string? state) => (state ?? string.Empty).Trim().ToLowerInvariant();
}
