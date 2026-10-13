using System.Globalization;
using System.Text.Json.Nodes;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.AzureDevOps;

/// <summary>
/// Provider-native tools. Every tool acts only on the session's current work item; the agent never
/// chooses a target id, which keeps mutation scope to the dispatched issue (SPEC 15.5).
/// </summary>
public static class AzureDevOpsTools
{
    public const string GetWorkItem = "get_work_item";
    public const string AddComment = "add_comment";
    public const string UpdateState = "update_state";
    public const string AddLink = "add_link";

    public static readonly IReadOnlyList<AgentToolSpec> Specs =
    [
        new(GetWorkItem,
            "Read the current Azure Boards work item (fields, tags, links) and its most recent comments.",
            Schema(new JsonObject
            {
                ["comment_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 50, ["description"] = "Recent comments to include (default 10)." },
            }),
            Mutates: false),
        new(AddComment,
            "Post a Markdown comment to the current work item's discussion.",
            Schema(new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Comment body (Markdown)." },
            }, "text"),
            Mutates: true),
        new(UpdateState,
            "Move the current work item to another workflow state (for example 'Resolved' or 'Done'). Fails if the process does not allow the transition. Explain the change with add_comment, not with reason.",
            Schema(new JsonObject
            {
                ["state"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional value for the Reason field. It must be one of the reasons the process defines for this transition (for example 'Code complete and unit tests pass'); free text is rejected. Omit it to use the default reason.",
                },
            }, "state"),
            Mutates: true),
        new(AddLink,
            "Attach a hyperlink (for example a pull request URL) to the current work item.",
            Schema(new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["format"] = "uri" },
                ["comment"] = new JsonObject { ["type"] = "string" },
            }, "url"),
            Mutates: true),
    ];

    public static async Task<AgentToolResult> ExecuteAsync(AzureDevOpsClient client, string name, JsonObject args, Issue issue, CancellationToken ct)
    {
        if (!int.TryParse(issue.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return AgentToolResult.Fail("invalid_context", "current issue id is not an Azure DevOps work item id");
        }

        try
        {
            switch (name)
            {
                case GetWorkItem:
                {
                    var count = Math.Clamp(IntArg(args, "comment_count") ?? 10, 0, 50);
                    var items = await client.GetWorkItemsAsync([id], expandRelations: true, fields: null, ct);
                    if (items.Count == 0)
                    {
                        return AgentToolResult.Fail("not_found", $"work item {id} is not visible");
                    }

                    var item = items[0];
                    var fields = item["fields"] as JsonObject ?? new JsonObject();
                    var output = new JsonObject
                    {
                        ["id"] = id,
                        ["identifier"] = issue.Identifier,
                        ["url"] = issue.Url,
                        ["type"] = WorkItemNormalizer.Str(fields["System.WorkItemType"]),
                        ["state"] = WorkItemNormalizer.Str(fields["System.State"]),
                        ["reason"] = WorkItemNormalizer.Str(fields["System.Reason"]),
                        ["title"] = WorkItemNormalizer.Str(fields["System.Title"]),
                        ["assigned_to"] = WorkItemNormalizer.Str((fields["System.AssignedTo"] as JsonObject)?["uniqueName"]),
                        ["tags"] = new JsonArray(WorkItemNormalizer.Labels(WorkItemNormalizer.Str(fields["System.Tags"])).Select(t => (JsonNode)t).ToArray()),
                        ["description"] = WorkItemNormalizer.Description(fields),
                        ["relations"] = (item["relations"] as JsonArray)?.DeepClone(),
                    };

                    if (count > 0)
                    {
                        var comments = await client.GetCommentsAsync(id, count, ct);
                        output["comments"] = new JsonArray((comments?["comments"] as JsonArray ?? [])
                            .OfType<JsonObject>()
                            .Select(c => (JsonNode)new JsonObject
                            {
                                ["author"] = WorkItemNormalizer.Str((c["createdBy"] as JsonObject)?["displayName"]),
                                ["created"] = WorkItemNormalizer.Str(c["createdDate"]),
                                ["text"] = Html.ToText(WorkItemNormalizer.Str(c["text"])) ?? WorkItemNormalizer.Str(c["text"]),
                            })
                            .ToArray());
                    }

                    return new AgentToolResult(true, output);
                }

                case AddComment:
                {
                    var text = StrArg(args, "text");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return AgentToolResult.Fail("invalid_arguments", "text is required");
                    }

                    var created = await client.AddCommentAsync(id, text, ct);
                    return new AgentToolResult(true, new JsonObject { ["comment_id"] = created?["id"]?.DeepClone(), ["work_item_id"] = id });
                }

                case UpdateState:
                {
                    var state = StrArg(args, "state");
                    if (string.IsNullOrWhiteSpace(state))
                    {
                        return AgentToolResult.Fail("invalid_arguments", "state is required");
                    }

                    var reason = StrArg(args, "reason") is { Length: > 0 } r ? r : null;
                    string? reasonError = null;
                    JsonNode? updated;
                    try
                    {
                        updated = await client.PatchWorkItemAsync(id, StatePatch(state.Trim(), reason), ct);
                    }
                    catch (TrackerException ex) when (reason is not null && ex.ProviderStatus == 400)
                    {
                        // Agents often pass a sentence as the reason. The state change is what matters, so
                        // retry with the process's default reason and report why the given one was dropped.
                        reasonError = ex.Message;
                        updated = await client.PatchWorkItemAsync(id, StatePatch(state.Trim(), reason: null), ct);
                    }

                    var output = new JsonObject
                    {
                        ["work_item_id"] = id,
                        ["state"] = WorkItemNormalizer.Str(updated?["fields"]?["System.State"]) ?? state,
                        ["rev"] = updated?["rev"]?.DeepClone(),
                    };
                    if (reasonError is not null)
                    {
                        output["reason_ignored"] = reasonError;
                    }

                    return new AgentToolResult(true, output);
                }

                case AddLink:
                {
                    var url = StrArg(args, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    {
                        return AgentToolResult.Fail("invalid_arguments", "url must be an absolute http(s) URL");
                    }

                    var relation = new JsonObject { ["rel"] = "Hyperlink", ["url"] = uri.ToString() };
                    if (StrArg(args, "comment") is { Length: > 0 } comment)
                    {
                        relation["attributes"] = new JsonObject { ["comment"] = comment };
                    }

                    var patch = new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/relations/-", ["value"] = relation });
                    await client.PatchWorkItemAsync(id, patch, ct);
                    return new AgentToolResult(true, new JsonObject { ["work_item_id"] = id, ["url"] = uri.ToString() });
                }

                default:
                    return AgentToolResult.Fail("unsupported_tool", $"unsupported tool: {name}");
            }
        }
        catch (TrackerException ex)
        {
            return AgentToolResult.Fail(ex.Category, ex.Message);
        }
    }

    private static JsonArray StatePatch(string state, string? reason)
    {
        var patch = new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = state });
        if (reason is not null)
        {
            patch.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Reason", ["value"] = reason });
        }

        return patch;
    }

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }

        return schema;
    }

    private static string? StrArg(JsonObject args, string key) => WorkItemNormalizer.Str(args[key]);

    private static int? IntArg(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;
}
