using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.Wrike;

/// <summary>
/// Provider-native tools. Every tool acts only on the session's current task; the agent never
/// chooses a target id, which keeps mutation scope to the dispatched issue (SPEC 15.5).
/// </summary>
public static partial class WrikeTools
{
    public const string GetTask = "get_task";
    public const string AddComment = "add_comment";
    public const string UpdateState = "update_state";
    public const string AddLink = "add_link";

    public static readonly IReadOnlyList<AgentToolSpec> Specs =
    [
        new(GetTask,
            "Read the current Wrike task (status, title, description, assignees, dependencies) and its most recent comments.",
            Schema(new JsonObject
            {
                ["comment_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 50, ["description"] = "Recent comments to include (default 10)." },
            }),
            Mutates: false),
        new(AddComment,
            "Post a comment to the current Wrike task. Markdown paragraphs, '- ' lists, **bold** and [links](https://…) are converted for Wrike.",
            Schema(new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Comment body (Markdown)." },
            }, "text"),
            Mutates: true),
        new(UpdateState,
            "Move the current Wrike task to another status of its workflow, by status name. An unknown name fails with the list of valid statuses. Explain the change with add_comment.",
            Schema(new JsonObject
            {
                ["state"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Status name, for example as listed in the workflow instructions." },
            }, "state"),
            Mutates: true),
        new(AddLink,
            "Attach a link (for example a pull request URL) to the current Wrike task. Wrike has no link fields, so this posts it as a comment.",
            Schema(new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["format"] = "uri" },
                ["comment"] = new JsonObject { ["type"] = "string", ["description"] = "Link text, for example 'Pull request'." },
            }, "url"),
            Mutates: true),
    ];

    public static async Task<AgentToolResult> ExecuteAsync(WrikeClient client, WrikeStatusMap statuses, WrikeOptions options,
        string name, JsonObject args, Issue issue, CancellationToken ct)
    {
        if (!WrikeOptions.IsTaskId(issue.Id))
        {
            return AgentToolResult.Fail("invalid_context", "current issue id is not a Wrike task id");
        }

        var id = issue.Id;
        try
        {
            switch (name)
            {
                case GetTask:
                {
                    var count = Math.Clamp(IntArg(args, "comment_count") ?? 10, 0, 50);
                    if (await client.GetTaskAsync(id, ct) is not { } task)
                    {
                        return AgentToolResult.Fail("not_found", $"task {issue.Identifier} is not visible");
                    }

                    var output = new JsonObject
                    {
                        ["id"] = id,
                        ["identifier"] = issue.Identifier,
                        ["url"] = WrikeNormalizer.Str(task["permalink"]) ?? issue.Url,
                        ["state"] = statuses.ById(WrikeNormalizer.Str(task["customStatusId"]))?.Name,
                        ["status_group"] = WrikeNormalizer.Str(task["status"]),
                        ["valid_states"] = new JsonArray(statuses.VisibleNames.Select(n => (JsonNode)n).ToArray()),
                        ["title"] = WrikeNormalizer.Str(task["title"]),
                        ["importance"] = WrikeNormalizer.Str(task["importance"]),
                        ["responsible_ids"] = new JsonArray(WrikeNormalizer.Strings(task["responsibleIds"]).Select(r => (JsonNode)r).ToArray()),
                        ["description"] = Html.ToText(WrikeNormalizer.Str(task["description"])),
                        ["blocked_by"] = new JsonArray(issue.BlockedBy.Select(b => (JsonNode)new JsonObject
                        {
                            ["identifier"] = b.Identifier,
                            ["state"] = b.State,
                        }).ToArray()),
                    };

                    if (count > 0)
                    {
                        var comments = await client.GetCommentsAsync(id, ct);
                        output["comments"] = new JsonArray(comments
                            .OrderByDescending(c => WrikeNormalizer.Str(c["createdDate"]), StringComparer.Ordinal)
                            .Take(count)
                            .Select(c => (JsonNode)new JsonObject
                            {
                                ["author_id"] = WrikeNormalizer.Str(c["authorId"]),
                                ["created"] = WrikeNormalizer.Str(c["createdDate"]),
                                ["text"] = Html.ToText(WrikeNormalizer.Str(c["text"])) ?? WrikeNormalizer.Str(c["text"]),
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

                    var created = await client.AddCommentAsync(id, MarkdownToHtml(text), ct);
                    return new AgentToolResult(true, new JsonObject { ["comment_id"] = WrikeNormalizer.Str(created?["id"]), ["task_id"] = id });
                }

                case UpdateState:
                {
                    var state = StrArg(args, "state");
                    if (string.IsNullOrWhiteSpace(state))
                    {
                        return AgentToolResult.Fail("invalid_arguments", "state is required");
                    }

                    if (!statuses.TryResolve(state.Trim(), out var status, out var error))
                    {
                        return AgentToolResult.Fail("invalid_arguments", error!);
                    }

                    var updated = await client.SetCustomStatusAsync(id, status!.Id, ct);
                    return new AgentToolResult(true, new JsonObject
                    {
                        ["task_id"] = id,
                        ["state"] = statuses.ById(WrikeNormalizer.Str(updated?["customStatusId"]))?.Name ?? status.Name,
                    });
                }

                case AddLink:
                {
                    var url = StrArg(args, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    {
                        return AgentToolResult.Fail("invalid_arguments", "url must be an absolute http(s) URL");
                    }

                    var label = StrArg(args, "comment") is { Length: > 0 } comment ? comment : uri.ToString();
                    var html = $"<a href=\"{WebUtility.HtmlEncode(uri.ToString())}\">{WebUtility.HtmlEncode(label)}</a>";
                    await client.AddCommentAsync(id, html, ct);
                    return new AgentToolResult(true, new JsonObject { ["task_id"] = id, ["url"] = uri.ToString() });
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

    /// <summary>
    /// The small Markdown subset agents write in summaries, as Wrike comment HTML: lines and blank lines become
    /// breaks, "- " / "* " lines become a list, **bold**, [text](url) and bare URLs become links. Everything else is escaped.
    /// </summary>
    public static string MarkdownToHtml(string markdown)
    {
        var html = new StringBuilder();
        var inList = false;
        var first = true;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var item = ListItem().Match(line);
            if (item.Success)
            {
                if (!inList)
                {
                    html.Append("<ul>");
                    inList = true;
                }

                html.Append("<li>").Append(Inline(item.Groups[1].Value)).Append("</li>");
                first = false;
                continue;
            }

            if (inList)
            {
                html.Append("</ul>");
                inList = false;
            }
            else if (!first)
            {
                html.Append("<br />");
            }

            html.Append(Inline(line));
            first = false;
        }

        if (inList)
        {
            html.Append("</ul>");
        }

        return html.ToString();
    }

    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = MarkdownLink().Replace(s, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        s = BareUrl().Replace(s, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        return Bold().Replace(s, "<b>$1</b>");
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

    private static string? StrArg(JsonObject args, string key) => WrikeNormalizer.Str(args[key]);

    private static int? IntArg(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex MarkdownLink();

    // Not inside an attribute or an existing anchor: preceded by start or whitespace.
    [GeneratedRegex(@"(?<=^|\s)https?://[^\s<]+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
