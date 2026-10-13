using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Wrike;

namespace Symphony.Tests;

/// <summary>
/// Minimal Wrike API v4 fake: batch and single task gets (a batch with an unknown id fails as Wrike does),
/// workflows, contacts, id conversion, dependencies, comments and status updates.
/// </summary>
public sealed class FakeWrike : HttpMessageHandler
{
    public const string Parent = "PARENT";
    public const string ParentPermalinkId = "4000";
    public const string Me = "KUAME";

    // Queue workflow, with Wrike's repeated names: a visible and a hidden "Analizado", two visible "Doble".
    public const string Active = "S_ACTIVE";
    public const string EnProceso = "S_PROC";
    public const string Analizado = "S_ANAL";
    public const string AnalizadoHidden = "S_ANAL_H";
    public const string Completed = "S_DONE";
    public const string Cancelled = "S_CANC";
    public const string DobleActive = "S_DOBLE_A";
    public const string DobleDeferred = "S_DOBLE_D";
    public const string Other = "S_OTHER";

    public Dictionary<string, JsonObject> Tasks { get; } = new(StringComparer.Ordinal);
    public List<(string Predecessor, string Successor)> Dependencies { get; } = [];
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
    public HttpStatusCode? ForceStatus { get; set; }
    public bool OtherWorkflowVisible { get; set; } = true;

    public FakeWrike()
    {
        Tasks[Parent] = new JsonObject
        {
            ["id"] = Parent,
            ["title"] = "Agent",
            ["customStatusId"] = Active,
            ["permalink"] = $"https://www.wrike.com/open.htm?id={ParentPermalinkId}",
            ["superTaskIds"] = new JsonArray(),
        };
    }

    public JsonObject Add(string id, string status, string? title = "Title", string importance = "Normal", string parent = Parent,
        string[]? responsibles = null, string updated = "2026-10-13T10:00:00Z", string description = "<p>Do <b>the</b> thing&nbsp;now</p><ul><li>one</li><li>two</li></ul>")
    {
        var task = new JsonObject
        {
            ["id"] = id,
            ["customStatusId"] = status,
            ["status"] = "Active",
            ["importance"] = importance,
            ["permalink"] = $"https://www.wrike.com/open.htm?id=9{Tasks.Count}",
            ["createdDate"] = "2026-10-01T09:00:00Z",
            ["updatedDate"] = updated,
            ["superTaskIds"] = new JsonArray((JsonNode)parent),
            ["responsibleIds"] = new JsonArray((responsibles ?? []).Select(r => (JsonNode)r).ToArray()),
            ["description"] = description,
        };
        if (title is not null) task["title"] = title;
        Tasks[id] = task;
        return task;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        lock (Requests) Requests.Add((request.Method, path + query, body));

        if (ForceStatus is { } status)
        {
            var r = Error(status, "too_many_requests", "nope");
            if (status == HttpStatusCode.TooManyRequests) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        }

        Assert.StartsWith("/api/v4/", path);
        var rest = path["/api/v4/".Length..];
        var parts = rest.Split('/');

        if (rest == "workflows") return Data("workflows", Workflows());
        if (rest == "contacts" && query == "?me=true") return Data("contacts", new JsonObject { ["id"] = Me, ["me"] = true });
        if (rest == "ids")
        {
            Assert.Equal($"?ids=[{ParentPermalinkId}]&type=ApiV2Task", query);
            return Data("ids", new JsonObject { ["id"] = Parent, ["apiV2Id"] = ParentPermalinkId });
        }

        if (parts[0] == "tasks" && parts.Length == 3 && parts[2] == "dependencies")
        {
            var id = parts[1];
            return Data("dependencies", Dependencies
                .Where(d => d.Predecessor == id || d.Successor == id)
                .Select(d => new JsonObject { ["id"] = $"D{d.Predecessor}{d.Successor}", ["predecessorId"] = d.Predecessor, ["successorId"] = d.Successor, ["relationType"] = "FinishToStart" })
                .ToArray());
        }

        if (parts[0] == "tasks" && parts.Length == 3 && parts[2] == "comments")
        {
            return request.Method == HttpMethod.Post
                ? Data("comments", new JsonObject { ["id"] = "C1", ["taskId"] = parts[1] })
                : Data("comments",
                    new JsonObject { ["id"] = "C0", ["authorId"] = "KUAX", ["createdDate"] = "2026-10-01T00:00:00Z", ["text"] = "<p>old</p>" },
                    new JsonObject { ["id"] = "C2", ["authorId"] = "KUAY", ["createdDate"] = "2026-10-02T00:00:00Z", ["text"] = "<p>new</p>" });
        }

        if (parts[0] == "tasks" && parts.Length == 2)
        {
            var ids = parts[1].Split(',');
            if (ids.Any(i => !Tasks.ContainsKey(i))) return Error(HttpStatusCode.BadRequest, "invalid_request", "Invalid Task ID");

            if (request.Method == HttpMethod.Put)
            {
                var form = System.Web.HttpUtility.ParseQueryString(body!);
                Tasks[ids[0]]["customStatusId"] = form["customStatus"];
                return Data("tasks", View(ids[0], single: true));
            }

            return Data("tasks", ids.Select(i => View(i, single: ids.Length == 1)).ToArray());
        }

        return Error(HttpStatusCode.BadRequest, "method_not_found", $"Method '{request.Method} {path}' is not supported");
    }

    /// <summary>Wrike computes subTaskIds and dependencyIds; only a single-task get includes the description.</summary>
    private JsonObject View(string id, bool single)
    {
        var task = (JsonObject)Tasks[id].DeepClone();
        task["subTaskIds"] = new JsonArray(Tasks.Values
            .Where(t => (t["superTaskIds"] as JsonArray)?.Any(p => p!.GetValue<string>() == id) == true)
            .Select(t => (JsonNode)t["id"]!.GetValue<string>())
            .ToArray());
        task["dependencyIds"] = new JsonArray(Dependencies
            .Where(d => d.Predecessor == id || d.Successor == id)
            .Select(d => (JsonNode)$"D{d.Predecessor}{d.Successor}")
            .ToArray());
        if (!single) task.Remove("description");
        return task;
    }

    private JsonObject[] Workflows()
    {
        static JsonObject S(string id, string name, string group, bool hidden = false) =>
            new() { ["id"] = id, ["name"] = name, ["group"] = group, ["hidden"] = hidden };

        var queue = new JsonObject
        {
            ["id"] = "W1",
            ["name"] = "ABSEAL I&N",
            ["customStatuses"] = new JsonArray(
                S(Active, "Active", "Active"), S(EnProceso, "En Proceso", "Active"), S(Analizado, "Analizado", "Active"),
                S(AnalizadoHidden, "Analizado", "Deferred", hidden: true), S(Completed, "Completed", "Completed"),
                S(Cancelled, "Cancelled", "Cancelled"), S(DobleActive, "Doble", "Active"), S(DobleDeferred, "Doble", "Deferred")),
        };
        var old = new JsonObject { ["id"] = "W1OLD", ["name"] = "ABSEAL I&N", ["hidden"] = true, ["customStatuses"] = new JsonArray() };
        var other = new JsonObject { ["id"] = "W2", ["name"] = "Otro flujo", ["customStatuses"] = new JsonArray(S(Other, "Otro", "Active")) };
        return OtherWorkflowVisible ? [queue, old, other] : [queue, old];
    }

    private static HttpResponseMessage Data(string kind, params JsonObject[] items) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["kind"] = kind, ["data"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()) }.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string description) =>
        new(status)
        {
            Content = new StringContent(new JsonObject { ["error"] = code, ["errorDescription"] = description }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
}

public class WrikeAdapterTests
{
    private static readonly Dictionary<string, string> Env = new() { ["WRIKE_TOKEN"] = "secret-token", ["MY_TOKEN"] = "other" };

    private static Dictionary<string, object?> Provider(string yaml) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)Symphony.Core.Workflow.WorkflowLoader.Parse(
            $"---\ntracker:\n  provider:\n{yaml}\n---\n").Config["tracker"]!)["provider"]!;

    private static (WrikeAdapter Adapter, FakeWrike Fake) Create(string extraProvider = "", IReadOnlyList<string>? active = null,
        IReadOnlyList<string>? terminal = null, string parent = FakeWrike.Parent)
    {
        var fake = new FakeWrike();
        var settings = new TrackerSettings("wrike", Provider($"    parent_task_id: \"{parent}\"\n{extraProvider}"), [],
            active ?? ["Active", "En Proceso"], terminal ?? ["Completed", "Cancelled"]);
        var adapter = (WrikeAdapter)new WrikeAdapterFactory(fake).Create(settings, n => Env.GetValueOrDefault(n), NullLoggerFactory.Instance);
        return (adapter, fake);
    }

    private static int Count(FakeWrike fake, Func<(HttpMethod Method, string Path, string? Body), bool> match) => fake.Requests.Count(match);

    [Fact]
    public async Task Fetch_by_states_returns_queue_subtasks_in_the_requested_states()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active, importance: "High");
        fake.Add("T2", FakeWrike.EnProceso);
        fake.Add("T3", FakeWrike.Analizado);
        fake.Add("T4", FakeWrike.Active, parent: "ELSEWHERE");

        var issues = await adapter.FetchIssuesByStatesAsync(["active", "En Proceso"], default);

        Assert.Equal(["T1", "T2"], issues.Select(i => i.Id).Order());
        var one = issues.Single(i => i.Id == "T1");
        Assert.Equal("WR-91", one.Identifier);
        Assert.Equal("Active", one.State);
        Assert.Equal(1, one.Priority);
        Assert.Equal(2, issues.Single(i => i.Id == "T2").Priority);
        Assert.Equal("https://www.wrike.com/open.htm?id=91", one.Url);
        Assert.Equal("Do the thing now\n\n- one\n- two", one.Description);
        Assert.Empty(one.Labels);
        Assert.True(one.Dispatchable);
        Assert.Equal("T1", one.NativeRef!["task_id"]!.GetValue<string>());
        Assert.Equal(FakeWrike.Parent, one.NativeRef["parent_task_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Permalink_parent_and_workflows_are_resolved_once()
    {
        var (adapter, fake) = Create(parent: $"https://www.wrike.com/open.htm?id={FakeWrike.ParentPermalinkId}");
        fake.Add("T1", FakeWrike.Active);

        await adapter.FetchIssuesByStatesAsync(["Active"], default);
        await adapter.FetchIssuesByStatesAsync(["Active"], default);

        Assert.Equal(1, Count(fake, r => r.Path.StartsWith("/api/v4/ids", StringComparison.Ordinal)));
        Assert.Equal(1, Count(fake, r => r.Path == "/api/v4/workflows"));
    }

    [Fact]
    public async Task Descriptions_are_fetched_once_per_task_revision()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        fake.Add("T2", FakeWrike.Active);
        bool SingleGet((HttpMethod Method, string Path, string? Body) r) => r.Method == HttpMethod.Get && r.Path == "/api/v4/tasks/T1";

        await adapter.FetchIssuesByStatesAsync(["Active"], default);
        await adapter.FetchIssuesByStatesAsync(["Active"], default);
        Assert.Equal(1, Count(fake, SingleGet));

        fake.Tasks["T1"]["updatedDate"] = "2026-10-14T00:00:00Z";
        fake.Tasks["T1"]["description"] = "<p>changed</p>";
        var issue = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single(i => i.Id == "T1");
        Assert.Equal(2, Count(fake, SingleGet));
        Assert.Equal("changed", issue.Description);
    }

    [Fact]
    public async Task Unknown_configured_state_fails_as_invalid_config()
    {
        var (adapter, _) = Create(active: ["Active", "Ready"]);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByStatesAsync(["Active"], default));

        Assert.Equal(TrackerErrorCategories.InvalidTrackerConfig, ex.Category);
        Assert.Contains("'Ready'", ex.Message);
        Assert.Contains("En Proceso", ex.Message);
    }

    [Fact]
    public async Task Missing_parent_task_fails_as_invalid_config()
    {
        var (adapter, _) = Create(parent: "NOPE");

        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByStatesAsync(["Active"], default));

        Assert.Equal(TrackerErrorCategories.InvalidTrackerConfig, ex.Category);
    }

    [Fact]
    public async Task Workflow_setting_picks_the_visible_workflow_by_name()
    {
        var (adapter, fake) = Create("    workflow: abseal i&n");
        fake.Add("T1", FakeWrike.Active);

        Assert.Single(await adapter.FetchIssuesByStatesAsync(["Active"], default));
    }

    [Fact]
    public async Task Assigned_to_me_marks_other_tasks_not_dispatchable()
    {
        var (adapter, fake) = Create("    assigned_to: \"@me\"");
        fake.Add("T1", FakeWrike.Active, responsibles: [FakeWrike.Me]);
        fake.Add("T2", FakeWrike.Active, responsibles: ["KUAOTHER"]);

        var issues = await adapter.FetchIssuesByStatesAsync(["Active"], default);

        Assert.True(issues.Single(i => i.Id == "T1").Dispatchable);
        Assert.False(issues.Single(i => i.Id == "T2").Dispatchable);
        Assert.Equal("Not assigned to @me", issues.Single(i => i.Id == "T2").HoldReason);
        Assert.Equal(FakeWrike.Me, issues.Single(i => i.Id == "T1").AssigneeId);
    }

    [Fact]
    public async Task Open_predecessors_block_dispatch_until_terminal()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        fake.Add("T2", FakeWrike.Active);
        fake.Add("PRE", FakeWrike.Analizado, parent: "ELSEWHERE");
        fake.Dependencies.Add(("PRE", "T1"));
        fake.Dependencies.Add(("T1", "T2"));

        var issues = await adapter.FetchIssuesByStatesAsync(["Active"], default);
        var one = issues.Single(i => i.Id == "T1");
        Assert.False(one.Dispatchable);
        var blocker = Assert.Single(one.BlockedBy);
        Assert.Equal(("PRE", "Analizado"), (blocker.Id, blocker.State));
        Assert.StartsWith("WR-", blocker.Identifier, StringComparison.Ordinal);
        Assert.Equal($"Blocked by {blocker.Identifier} (Analizado)", one.HoldReason);
        // T2 is blocked by T1, which is still Active.
        Assert.False(issues.Single(i => i.Id == "T2").Dispatchable);

        fake.Tasks["PRE"]["customStatusId"] = FakeWrike.Completed;
        issues = await adapter.FetchIssuesByStatesAsync(["Active"], default);
        Assert.True(issues.Single(i => i.Id == "T1").Dispatchable);
    }

    [Fact]
    public async Task Predecessors_do_not_block_when_blocking_is_off()
    {
        var (adapter, fake) = Create("    block_on_predecessors: false");
        fake.Add("T1", FakeWrike.Active);
        fake.Add("PRE", FakeWrike.Active);
        fake.Dependencies.Add(("PRE", "T1"));

        var one = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single(i => i.Id == "T1");

        Assert.True(one.Dispatchable);
        Assert.Null(one.HoldReason);
        Assert.Single(one.BlockedBy);
    }

    [Fact]
    public async Task Fetch_by_ids_skips_deleted_and_out_of_queue_tasks()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Analizado);
        fake.Add("T2", FakeWrike.Active, parent: "ELSEWHERE");
        fake.Add("T3", FakeWrike.Completed);

        var issues = await adapter.FetchIssuesByIdsAsync(["T1", "GONE", "T2", "T3", "bad/id"], default);

        Assert.Equal(["T1", "T3"], issues.Select(i => i.Id).Order());
        Assert.Equal("Analizado", issues.Single(i => i.Id == "T1").State);
        Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_status_added_after_startup_reloads_the_workflows()
    {
        var (adapter, fake) = Create();
        fake.OtherWorkflowVisible = false;
        fake.Add("T1", FakeWrike.Active);
        await adapter.FetchIssuesByStatesAsync(["Active"], default);

        fake.OtherWorkflowVisible = true;
        fake.Add("T2", FakeWrike.Other);
        var issues = await adapter.FetchIssuesByIdsAsync(["T2"], default);

        Assert.Equal("Otro", issues.Single().State);
        Assert.Equal(2, Count(fake, r => r.Path == "/api/v4/workflows"));
    }

    [Fact]
    public async Task Update_state_maps_names_to_the_visible_status_of_the_queue_workflow()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        var issue = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single();

        var result = await adapter.ExecuteAgentToolAsync(WrikeTools.UpdateState, new JsonObject { ["state"] = "analizado" }, issue, default);

        Assert.True(result.Success);
        Assert.Equal(FakeWrike.Analizado, fake.Tasks["T1"]["customStatusId"]!.GetValue<string>());
        Assert.Equal("Analizado", result.Output!["state"]!.GetValue<string>());
        Assert.Contains(fake.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/v4/tasks/T1");
    }

    [Theory]
    [InlineData("Resolved", "not a status")]
    [InlineData("Doble", "matches 2 statuses")]
    public async Task Update_state_rejects_unknown_and_ambiguous_names(string state, string message)
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        var issue = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single();

        var result = await adapter.ExecuteAgentToolAsync(WrikeTools.UpdateState, new JsonObject { ["state"] = state }, issue, default);

        Assert.False(result.Success);
        Assert.Contains(message, result.Output!["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Comment_and_link_tools_post_html_to_the_current_task()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        var issue = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single();

        Assert.True((await adapter.ExecuteAgentToolAsync(WrikeTools.AddComment, new JsonObject { ["text"] = "**Done**\n- a <b>" }, issue, default)).Success);
        Assert.True((await adapter.ExecuteAgentToolAsync(WrikeTools.AddLink, new JsonObject { ["url"] = "https://dev.azure.com/pr/1", ["comment"] = "PR" }, issue, default)).Success);

        var posts = fake.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.All(posts, p => Assert.Equal("/api/v4/tasks/T1/comments", p.Path));
        Assert.Equal("<b>Done</b><ul><li>a &lt;b&gt;</li></ul>", System.Web.HttpUtility.ParseQueryString(posts[0].Body!)["text"]);
        Assert.Equal("<a href=\"https://dev.azure.com/pr/1\">PR</a>", System.Web.HttpUtility.ParseQueryString(posts[1].Body!)["text"]);
    }

    [Fact]
    public async Task Get_task_returns_description_valid_states_and_latest_comments()
    {
        var (adapter, fake) = Create();
        fake.Add("T1", FakeWrike.Active);
        var issue = (await adapter.FetchIssuesByStatesAsync(["Active"], default)).Single();

        var result = await adapter.ExecuteAgentToolAsync(WrikeTools.GetTask, new JsonObject { ["comment_count"] = 1 }, issue, default);

        Assert.True(result.Success);
        var output = result.Output!;
        Assert.Equal("Active", output["state"]!.GetValue<string>());
        Assert.Equal("Do the thing now\n\n- one\n- two", output["description"]!.GetValue<string>());
        Assert.Contains("Analizado", output["valid_states"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("new", output["comments"]!.AsArray().Single()!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_reject_an_issue_that_is_not_a_wrike_task()
    {
        var (adapter, _) = Create();
        var issue = new Issue { Id = "../contacts", Identifier = "X", Title = "x", State = "Active", Dispatchable = true };

        var result = await adapter.ExecuteAgentToolAsync(WrikeTools.AddComment, new JsonObject { ["text"] = "hi" }, issue, default);

        Assert.False(result.Success);
        Assert.Equal("invalid_context", result.Output!["error"]!["category"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerErrorCategories.Status)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerErrorCategories.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerErrorCategories.Status)]
    public async Task Http_failures_map_to_portable_categories_without_the_token(HttpStatusCode status, string category)
    {
        var (adapter, fake) = Create();
        fake.ForceStatus = status;

        var ex = await Assert.ThrowsAsync<TrackerException>(() => adapter.FetchIssuesByStatesAsync(["Active"], default));

        Assert.Equal(category, ex.Category);
        Assert.Equal((int)status, ex.ProviderStatus);
        Assert.DoesNotContain("secret-token", ex.Message);
        if (status == HttpStatusCode.TooManyRequests) Assert.Equal(7000, ex.RetryAfterMs);
    }

    [Fact]
    public void Secret_env_names_include_the_configured_token_variable()
    {
        var (adapter, _) = Create("    token: $MY_TOKEN");

        Assert.Equal(["WRIKE_TOKEN", "WRIKE_ACCESS_TOKEN", "MY_TOKEN"], adapter.SecretEnvironmentNames());
        Assert.Equal("other", adapter.Options.Token);
    }

    [Theory]
    [InlineData("    parent_task_id: \"X\"\n    host: evil.example.com", TrackerErrorCategories.InvalidTrackerConfig)]
    [InlineData("    parent_task_id: \"X\"\n    host: http://www.wrike.com", TrackerErrorCategories.InvalidTrackerConfig)]
    [InlineData("    host: www.wrike.com", TrackerErrorCategories.InvalidTrackerConfig)]
    [InlineData("    parent_task_id: \"a b\"", TrackerErrorCategories.InvalidTrackerConfig)]
    [InlineData("    parent_task_id: \"X\"\n    token: $UNSET_VAR", TrackerErrorCategories.MissingTrackerSecret)]
    public void Invalid_provider_settings_are_rejected(string yaml, string category)
    {
        var ex = Assert.Throws<TrackerException>(() => new WrikeAdapterFactory().ValidateProvider(Provider(yaml), n => Env.GetValueOrDefault(n)));
        Assert.Equal(category, ex.Category);
    }

    [Theory]
    [InlineData("https://www.wrike.com/open.htm?id=4583302343", "4583302343")]
    [InlineData("4583302343", "4583302343")]
    [InlineData("MAAAAAEQ-60y", "MAAAAAEQ-60y")]
    public void Parent_task_id_accepts_ids_and_permalinks(string value, string expected)
    {
        var options = WrikeOptions.Parse(Provider($"    parent_task_id: \"{value}\"\n    host: app-eu.wrike.com"), n => Env.GetValueOrDefault(n));

        Assert.Equal(expected, options.ParentTask);
        Assert.Equal(new Uri("https://app-eu.wrike.com/api/v4/"), options.ApiBase);
    }

    [Fact]
    public void Markdown_comments_become_wrike_html()
    {
        var html = WrikeTools.MarkdownToHtml("Summary with **bold** and [PR](https://x.test/pr?a=1&b=2)\n\n- one\n* two\nSee https://x.test/log");

        Assert.Equal(
            "Summary with <b>bold</b> and <a href=\"https://x.test/pr?a=1&amp;b=2\">PR</a><br /><ul><li>one</li><li>two</li></ul>See <a href=\"https://x.test/log\">https://x.test/log</a>",
            html);
    }

    [Fact]
    public void Markdown_code_spans_become_code_and_are_not_formatted()
    {
        var html = WrikeTools.MarkdownToHtml("Run `dotnet test **x** https://x.test <a>` then **done**, and a stray ` stays");

        Assert.Equal(
            "Run <code>dotnet test **x** https://x.test &lt;a&gt;</code> then <b>done</b>, and a stray ` stays",
            html);
    }
}

/// <summary>Read-only check against a real Wrike account. Skipped unless SYMPHONY_REAL_WRIKE=1.</summary>
public sealed class RealWrikeFactAttribute : FactAttribute
{
    public RealWrikeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SYMPHONY_REAL_WRIKE") != "1")
        {
            Skip = "set SYMPHONY_REAL_WRIKE=1, WRIKE_TOKEN and WRIKE_PARENT_TASK (id or permalink) to read a real Wrike queue";
        }
    }
}

public class RealWrikeTests
{
    [RealWrikeFact]
    public async Task Real_wrike_queue_reads_without_writes()
    {
        var parent = Environment.GetEnvironmentVariable("WRIKE_PARENT_TASK") ?? throw new InvalidOperationException("WRIKE_PARENT_TASK is not set");
        var active = (Environment.GetEnvironmentVariable("WRIKE_ACTIVE_STATES") ?? "Active").Split(',', StringSplitOptions.TrimEntries);
        var provider = new Dictionary<string, object?> { ["parent_task_id"] = parent };
        var settings = new TrackerSettings("wrike", provider, [], active, ["Completed", "Cancelled"]);
        using var adapter = new WrikeAdapterFactory().Create(settings, Environment.GetEnvironmentVariable, NullLoggerFactory.Instance);

        var issues = await adapter.FetchIssuesByStatesAsync(active, default);
        var again = await adapter.FetchIssuesByIdsAsync(issues.Select(i => i.Id).ToList(), default);

        Assert.Equal(issues.Select(i => i.Id).Order(), again.Select(i => i.Id).Order());
        Assert.All(issues, i => Assert.StartsWith("WR-", i.Identifier, StringComparison.Ordinal));
    }
}
