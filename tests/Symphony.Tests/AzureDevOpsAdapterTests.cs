using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.AzureDevOps;
using Symphony.Core.Config;
using Symphony.Core.Tracker;

namespace Symphony.Tests;

/// <summary>Minimal Azure DevOps fake: WIQL with keyset paging, workitemsbatch, connectionData, PATCH, comments.</summary>
public sealed class FakeAzureDevOps : HttpMessageHandler
{
    public Dictionary<int, JsonObject> Items { get; } = new();
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
    public HttpStatusCode? ForceStatus { get; set; }
    public bool ForceHtml { get; set; }
    public string MeId { get; set; } = "me-guid";

    public static JsonObject Item(int id, string state, string? title = "Title", string project = "Proj", string type = "Task",
        int? priority = null, string? tags = null, string? assignedId = null, string? assignedName = null, int[]? predecessors = null,
        string? area = null, string? created = "2025-01-02T03:04:05Z")
    {
        var fields = new JsonObject
        {
            ["System.Id"] = id,
            ["System.State"] = state,
            ["System.TeamProject"] = project,
            ["System.WorkItemType"] = type,
            ["System.AreaPath"] = area ?? project,
            ["System.Description"] = "<div>Do <b>the</b> thing&nbsp;now</div><ul><li>one</li><li>two</li></ul>",
            ["System.CreatedDate"] = created,
            ["System.ChangedDate"] = "2025-02-02T00:00:00Z",
        };
        if (title is not null) fields["System.Title"] = title;
        if (priority is not null) fields["Microsoft.VSTS.Common.Priority"] = priority;
        if (tags is not null) fields["System.Tags"] = tags;
        if (assignedId is not null || assignedName is not null)
        {
            fields["System.AssignedTo"] = new JsonObject { ["id"] = assignedId, ["uniqueName"] = assignedName, ["displayName"] = assignedName };
        }

        var item = new JsonObject { ["id"] = id, ["rev"] = 3, ["fields"] = fields };
        if (predecessors is not null)
        {
            item["relations"] = new JsonArray(predecessors.Select(p => (JsonNode)new JsonObject
            {
                ["rel"] = WorkItemNormalizer.PredecessorRel,
                ["url"] = $"https://dev.azure.com/org/_apis/wit/workItems/{p}",
            }).ToArray());
        }

        return item;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add((request.Method, path + request.RequestUri.Query, body));

        if (ForceStatus is { } status)
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"nope\"}", Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.TooManyRequests) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        }

        if (ForceHtml)
        {
            return new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html") };
        }

        if (path.EndsWith("/_apis/wit/wiql"))
        {
            var query = JsonNode.Parse(body!)!["query"]!.GetValue<string>();
            var top = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["$top"]!);
            var after = int.Parse(System.Text.RegularExpressions.Regex.Match(query, @"\[System\.Id\] > (\d+)").Groups[1].Value);
            var states = System.Text.RegularExpressions.Regex.Match(query, @"\[System\.State\] IN \(([^)]*)\)").Groups[1].Value
                .Split(',').Select(s => s.Trim().Trim('\'').ToLowerInvariant()).ToHashSet();
            var ids = Items.Values
                .Where(i => states.Contains(i["fields"]!["System.State"]!.GetValue<string>().ToLowerInvariant()))
                .Select(i => i["id"]!.GetValue<int>())
                .Where(id => id > after)
                .Order()
                .Take(top)
                .ToList();
            return Json(new JsonObject { ["workItems"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()) });
        }

        if (path.EndsWith("/_apis/wit/workitemsbatch"))
        {
            var req = JsonNode.Parse(body!)!;
            var ids = req["ids"]!.AsArray().Select(n => n!.GetValue<int>());
            // Return in reverse order to prove the adapter restores query order.
            var value = ids.Where(Items.ContainsKey).Reverse().Select(i => (JsonNode)Items[i].DeepClone()).ToArray();
            return Json(new JsonObject { ["count"] = value.Length, ["value"] = new JsonArray(value) });
        }

        if (path.EndsWith("/_apis/connectionData"))
        {
            return Json(new JsonObject { ["authenticatedUser"] = new JsonObject { ["id"] = MeId } });
        }

        if (request.Method == HttpMethod.Patch && path.Contains("/_apis/wit/workitems/"))
        {
            var id = int.Parse(path.Split('/').Last());
            var item = Items[id];
            foreach (var op in JsonNode.Parse(body!)!.AsArray())
            {
                if (op!["path"]!.GetValue<string>() == "/fields/System.State")
                {
                    item["fields"]!["System.State"] = op["value"]!.GetValue<string>();
                }
            }

            return Json(item.DeepClone());
        }

        if (path.Contains("/comments"))
        {
            return request.Method == HttpMethod.Post
                ? Json(new JsonObject { ["id"] = 99 })
                : Json(new JsonObject { ["comments"] = new JsonArray(new JsonObject { ["text"] = "<p>hi</p>", ["createdBy"] = new JsonObject { ["displayName"] = "Ana" } }) });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

public class AzureDevOpsAdapterTests
{
    private static readonly Dictionary<string, string> Env = new() { ["AZURE_DEVOPS_PAT"] = "secret-pat", ["MY_PAT"] = "other" };

    private static (AzureDevOpsAdapter Adapter, FakeAzureDevOps Fake) Create(string extraProvider = "", IReadOnlyList<string>? terminal = null)
    {
        var fake = new FakeAzureDevOps();
        var provider = (Dictionary<string, object?>)((Dictionary<string, object?>)Symphony.Core.Workflow.WorkflowLoader.Parse(
            $"---\ntracker:\n  provider:\n    organization_url: https://dev.azure.com/org\n    project: Proj\n    wiql_page_size: 2\n{extraProvider}\n---\n").Config["tracker"]!)["provider"]!;
        var settings = new TrackerSettings("azure_devops", provider, [], ["To Do", "Doing"], terminal ?? ["Done", "Removed"]);
        var adapter = (AzureDevOpsAdapter)new AzureDevOpsAdapterFactory(fake).Create(settings, n => Env.GetValueOrDefault(n), NullLoggerFactory.Instance);
        return (adapter, fake);
    }

    [Fact]
    public async Task Fetch_by_states_pages_and_normalizes_in_id_order()
    {
        var (adapter, fake) = Create();
        fake.Items[5] = FakeAzureDevOps.Item(5, "To Do", priority: 2, tags: "Backend; API ;backend; ");
        fake.Items[3] = FakeAzureDevOps.Item(3, "doing");
        fake.Items[9] = FakeAzureDevOps.Item(9, "To Do");
        fake.Items[7] = FakeAzureDevOps.Item(7, "Done");

        var issues = await adapter.FetchIssuesByStatesAsync(["To Do", "Doing"], default);

        Assert.Equal(["3", "5", "9"], issues.Select(i => i.Id));
        Assert.Equal(2, fake.Requests.Count(r => r.Path.Contains("/wiql")));
        var five = issues[1];
        Assert.Equal("WI-5", five.Identifier);
        Assert.Equal(2, five.Priority);
        Assert.Equal(["backend", "api"], five.Labels);
        Assert.Equal("To Do", five.State);
        Assert.True(five.Dispatchable);
        Assert.Equal("https://dev.azure.com/org/Proj/_workitems/edit/5", five.Url);
        Assert.Equal("Do the thing now\n\n- one\n- two", five.Description);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T03:04:05Z"), five.CreatedAt);
        Assert.Equal(5, five.NativeRef!["work_item_id"]!.GetValue<int>());
        Assert.Equal("Task", five.NativeRef["work_item_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wiql_quotes_values_and_applies_scope_filters()
    {
        var (adapter, fake) = Create("    area_path: \"Proj\\\\Team A\"\n    work_item_types: [Bug, \"User Story\"]");
        await adapter.FetchIssuesByStatesAsync(["Won't Fix"], default);
        var query = JsonNode.Parse(fake.Requests.Single().Body!)!["query"]!.GetValue<string>();
        Assert.Contains("[System.State] IN ('Won''t Fix')", query);
        Assert.Contains("[System.AreaPath] UNDER 'Proj\\Team A'", query);
        Assert.Contains("[System.WorkItemType] IN ('Bug', 'User Story')", query);
        Assert.Contains("[System.TeamProject] = @project", query);
    }

    [Fact]
    public async Task Empty_inputs_make_no_requests()
    {
        var (adapter, fake) = Create();
        Assert.Empty(await adapter.FetchIssuesByStatesAsync([], default));
        Assert.Empty(await adapter.FetchIssuesByIdsAsync([], default));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Malformed_records_are_omitted_from_state_lists_but_fail_id_refresh()
    {
        var (adapter, fake) = Create();
        fake.Items[1] = FakeAzureDevOps.Item(1, "To Do", title: null);
        fake.Items[2] = FakeAzureDevOps.Item(2, "To Do");

        Assert.Equal(["2"], (await adapter.FetchIssuesByStatesAsync(["To Do"], default)).Select(i => i.Id));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByIdsAsync(["1", "2"], default));
        Assert.Equal(TrackerErrorCategories.Response, ex.Category);
    }

    [Fact]
    public async Task Fetch_by_ids_omits_missing_and_out_of_scope_items()
    {
        var (adapter, fake) = Create();
        fake.Items[1] = FakeAzureDevOps.Item(1, "Done");
        fake.Items[2] = FakeAzureDevOps.Item(2, "To Do", project: "Other");

        var issues = await adapter.FetchIssuesByIdsAsync(["1", "2", "3", "not-a-number"], default);

        Assert.Equal("1", Assert.Single(issues).Id);
        Assert.Equal("Done", issues[0].State);
    }

    [Fact]
    public async Task Assigned_to_me_controls_dispatchable()
    {
        var (adapter, fake) = Create("    assigned_to: \"@Me\"");
        fake.Items[1] = FakeAzureDevOps.Item(1, "To Do", assignedId: "me-guid", assignedName: "me@x.com");
        fake.Items[2] = FakeAzureDevOps.Item(2, "To Do", assignedId: "bob-guid", assignedName: "bob@x.com");
        fake.Items[3] = FakeAzureDevOps.Item(3, "To Do");

        var issues = await adapter.FetchIssuesByStatesAsync(["To Do"], default);

        Assert.Equal([true, false, false], issues.Select(i => i.Dispatchable));
        Assert.Equal(3, issues.Count);
        Assert.Single(fake.Requests, r => r.Path.Contains("connectionData"));
    }

    [Fact]
    public async Task Assigned_to_unique_name_matches_case_insensitively()
    {
        var (adapter, fake) = Create("    assigned_to: Ana@Contoso.com");
        fake.Items[1] = FakeAzureDevOps.Item(1, "To Do", assignedId: "x", assignedName: "ana@contoso.com");
        Assert.True((await adapter.FetchIssuesByStatesAsync(["To Do"], default))[0].Dispatchable);
    }

    [Fact]
    public async Task Open_predecessors_block_dispatch()
    {
        var (adapter, fake) = Create();
        fake.Items[1] = FakeAzureDevOps.Item(1, "To Do", predecessors: [10]);
        fake.Items[2] = FakeAzureDevOps.Item(2, "To Do", predecessors: [11]);
        fake.Items[10] = FakeAzureDevOps.Item(10, "Doing");
        fake.Items[11] = FakeAzureDevOps.Item(11, "Done");

        var issues = (await adapter.FetchIssuesByStatesAsync(["To Do"], default)).ToDictionary(i => i.Id);

        Assert.False(issues["1"].Dispatchable);
        Assert.Equal(new BlockerRefView("10", "WI-10", "Doing"), BlockerRefView.From(issues["1"].BlockedBy.Single()));
        Assert.True(issues["2"].Dispatchable);
    }

    [Fact]
    public async Task Predecessor_blocking_can_be_disabled()
    {
        var (adapter, fake) = Create("    block_on_predecessors: false");
        fake.Items[1] = FakeAzureDevOps.Item(1, "To Do", predecessors: [10]);
        fake.Items[10] = FakeAzureDevOps.Item(10, "Doing");
        Assert.True((await adapter.FetchIssuesByStatesAsync(["To Do"], default))[0].Dispatchable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerErrorCategories.Status)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerErrorCategories.Status)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerErrorCategories.RateLimited)]
    public async Task Http_errors_map_to_categories(HttpStatusCode status, string category)
    {
        var (adapter, fake) = Create();
        fake.ForceStatus = status;
        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByStatesAsync(["To Do"], default));
        Assert.Equal(category, ex.Category);
        Assert.DoesNotContain("secret-pat", ex.Message);
        if (status == HttpStatusCode.TooManyRequests) Assert.Equal(7000, ex.RetryAfterMs);
    }

    [Fact]
    public async Task Html_sign_in_page_is_an_auth_failure()
    {
        var (adapter, fake) = Create();
        fake.ForceHtml = true;
        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByIdsAsync(["1"], default));
        Assert.Equal(TrackerErrorCategories.Status, ex.Category);
    }

    [Fact]
    public void Provider_validation_and_secret_resolution()
    {
        Dictionary<string, object?> P(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);
        Func<string, string?> env = n => Env.GetValueOrDefault(n);

        var ok = AzureDevOpsOptions.Parse(P(("organization_url", "https://dev.azure.com/org/"), ("project", "Proj")), env);
        Assert.Equal("secret-pat", ok.Token);
        Assert.Equal("AZURE_DEVOPS_PAT", ok.TokenEnvName);

        var custom = AzureDevOpsOptions.Parse(P(("organization_url", "https://dev.azure.com/org"), ("project", "Proj"), ("token", "$MY_PAT")), env);
        Assert.Equal("other", custom.Token);
        Assert.Equal("MY_PAT", custom.TokenEnvName);

        Assert.Equal(TrackerErrorCategories.MissingTrackerSecret, Assert.Throws<TrackerException>(() =>
            AzureDevOpsOptions.Parse(P(("organization_url", "https://dev.azure.com/org"), ("project", "Proj"), ("token", "$EMPTY")), env)).Category);
        Assert.Equal(TrackerErrorCategories.InvalidTrackerConfig, Assert.Throws<TrackerException>(() =>
            AzureDevOpsOptions.Parse(P(("project", "Proj")), env)).Category);
        Assert.Equal(TrackerErrorCategories.InvalidTrackerConfig, Assert.Throws<TrackerException>(() =>
            AzureDevOpsOptions.Parse(P(("organization_url", "not a url"), ("project", "Proj")), env)).Category);
    }

    [Fact]
    public void Secret_env_names_include_configured_token_var()
    {
        var (adapter, _) = Create("    token: $MY_PAT");
        Assert.Contains("MY_PAT", adapter.SecretEnvironmentNames());
        Assert.Contains("AZURE_DEVOPS_PAT", adapter.SecretEnvironmentNames());
        Assert.Contains("SYSTEM_ACCESSTOKEN", adapter.SecretEnvironmentNames());
    }

    [Fact]
    public async Task Tools_act_only_on_the_current_work_item()
    {
        var (adapter, fake) = Create();
        fake.Items[4] = FakeAzureDevOps.Item(4, "Doing");
        var issue = (await adapter.FetchIssuesByIdsAsync(["4"], default))[0];

        var state = await adapter.ExecuteAgentToolAsync(AzureDevOpsTools.UpdateState, new JsonObject { ["state"] = "Done", ["id"] = 999 }, issue, default);
        Assert.True(state.Success);
        Assert.Equal("Done", fake.Items[4]["fields"]!["System.State"]!.GetValue<string>());
        Assert.Contains(fake.Requests, r => r.Method == HttpMethod.Patch && r.Path.Contains("/workitems/4"));

        var comment = await adapter.ExecuteAgentToolAsync(AzureDevOpsTools.AddComment, new JsonObject { ["text"] = "**done**" }, issue, default);
        Assert.True(comment.Success);
        Assert.Contains(fake.Requests, r => r.Method == HttpMethod.Post && r.Path.Contains("/workItems/4/comments") && r.Path.Contains("format=markdown"));

        var read = await adapter.ExecuteAgentToolAsync(AzureDevOpsTools.GetWorkItem, new JsonObject(), issue, default);
        Assert.True(read.Success);
        Assert.Equal("hi", read.Output!["comments"]![0]!["text"]!.GetValue<string>());

        var bad = await adapter.ExecuteAgentToolAsync(AzureDevOpsTools.AddLink, new JsonObject { ["url"] = "javascript:alert(1)" }, issue, default);
        Assert.False(bad.Success);

        var unknown = await adapter.ExecuteAgentToolAsync("delete_everything", new JsonObject(), issue, default);
        Assert.False(unknown.Success);
        Assert.Equal("unsupported_tool", unknown.Output!["error"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_errors_are_structured_failures()
    {
        var (adapter, fake) = Create();
        fake.Items[4] = FakeAzureDevOps.Item(4, "Doing");
        var issue = (await adapter.FetchIssuesByIdsAsync(["4"], default))[0];
        fake.ForceStatus = HttpStatusCode.BadRequest;
        var result = await adapter.ExecuteAgentToolAsync(AzureDevOpsTools.UpdateState, new JsonObject { ["state"] = "Nope" }, issue, default);
        Assert.False(result.Success);
        Assert.Equal(TrackerErrorCategories.Status, result.Output!["error"]!["category"]!.GetValue<string>());
    }

    private sealed record BlockerRefView(string? Id, string? Identifier, string? State)
    {
        public static BlockerRefView From(Symphony.Core.Domain.BlockerRef b) => new(b.Id, b.Identifier, b.State);
    }
}
