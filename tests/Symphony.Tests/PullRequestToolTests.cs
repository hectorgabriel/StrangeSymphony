using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.AzureDevOps;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.PullRequests;
using Symphony.Core.Tracker;
using Symphony.Core.Util;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>A bare "remote" plus a clone on branch <c>feature</c>, with <c>main</c> pushed.</summary>
internal sealed class GitSandbox : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("symphony-pr-").FullName;
    public string Remote => Path.Combine(Dir, "remote.git");
    public string Work => Path.Combine(Dir, "work");

    public GitSandbox()
    {
        Git(Dir, "init", "--quiet", "--bare", "-b", "main", Remote);
        Git(Dir, "clone", "--quiet", Remote, Work);
        Git(Work, "config", "user.email", "test@example.com");
        Git(Work, "config", "user.name", "Test");
        Git(Work, "config", "core.autocrlf", "false");
        Git(Work, "checkout", "--quiet", "-b", "main");
        Commit("README.md", "hello\n");
        Git(Work, "push", "--quiet", "-u", "origin", "main");
        Git(Work, "checkout", "--quiet", "-b", "feature");
    }

    public void Commit(string path, string content)
    {
        var full = Path.Combine(Work, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git(Work, "add", "--", path);
        Git(Work, "commit", "--quiet", "-m", $"add {path}");
    }

    public void Push() => Git(Work, "push", "--quiet", "-u", "origin", "HEAD");

    public string Head() => Git(Work, "rev-parse", "HEAD").Trim();

    public static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? stdout.Result : throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
    }

    public void Dispose()
    {
        try
        {
            Symphony.Core.Workspaces.WorkspaceManager.DeleteDirectory(Dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FakePullRequestProvider : IPullRequestProvider
{
    public List<PullRequestDraft> Created { get; } = [];
    public PullRequestInfo? Existing { get; set; }
    public int MaxDescriptionLength { get; set; } = 4000;
    public IReadOnlyList<string> SecretEnvironmentNames => ["FAKE_PR_TOKEN"];
    public bool Disposed { get; private set; }

    public Task<PullRequestInfo?> FindActiveAsync(string sourceBranch, string targetBranch, CancellationToken ct) => Task.FromResult(Existing);

    public Task<PullRequestInfo> CreateAsync(PullRequestDraft draft, CancellationToken ct)
    {
        Created.Add(draft);
        return Task.FromResult(new PullRequestInfo("7", "https://example.test/pr/7", draft.IsDraft));
    }

    public void Dispose() => Disposed = true;
}

public sealed class PullRequestToolTests : IDisposable
{
    private readonly GitSandbox _git = new();
    private readonly FakePullRequestProvider _provider = new();

    private PullRequestTool Tool(string verify = "exit 0", int maxFiles = 200, IReadOnlyList<string>? forbidden = null) =>
        new(new PullRequestSettings("fake", new Dictionary<string, object?>(), "origin", "main", Draft: true, verify, VerifyTimeoutMs: 60000, maxFiles,
                forbidden ?? ConfigBuilder.DefaultForbiddenPaths),
            _provider, _git.Work, ShellRunner.Auto, NullLogger<PullRequestTool>.Instance);

    private static Task<AgentToolResult> Call(PullRequestTool tool, string title = "T02: motivos", string description = "Cambia el catálogo.") =>
        tool.ExecuteAsync(PullRequestTool.Name, new JsonObject { ["title"] = title, ["description"] = description }, TestIssues.Make("1"), default);

    private static string Category(AgentToolResult r) => r.Output!["error"]!["category"]!.GetValue<string>();

    private static string Message(AgentToolResult r) => r.Output!["error"]!["message"]!.GetValue<string>();

    [Fact]
    public async Task Opens_a_draft_pull_request_once_the_pushed_commit_verifies()
    {
        _git.Commit("src/a.cs", "class A {}\n");
        _git.Push();

        var result = await Call(Tool());

        Assert.True(result.Success, result.Output?.ToJsonString());
        Assert.True(result.Output!["created"]!.GetValue<bool>());
        Assert.Equal(_git.Head(), result.Output["verified_commit"]!.GetValue<string>());
        var draft = Assert.Single(_provider.Created);
        Assert.Equal(("feature", "main", true), (draft.SourceBranch, draft.TargetBranch, draft.IsDraft));
        Assert.StartsWith("Cambia el catálogo.", draft.Description, StringComparison.Ordinal);
        Assert.Contains($"Commit: `{_git.Head()[..7]}` on `feature`", draft.Description, StringComparison.Ordinal);
        Assert.Contains("`exit 0` exited 0", draft.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_verification_creates_nothing_and_returns_the_end_of_the_output()
    {
        _git.Commit("src/a.cs", "class A {}\n");
        _git.Push();

        var result = await Call(Tool("for i in $(seq 1 3000); do echo line $i; done; echo 'error CS1002: ; expected'; exit 3"));

        Assert.False(result.Success);
        Assert.Equal("verification_failed", Category(result));
        Assert.Contains("exited with code 3", Message(result), StringComparison.Ordinal);
        // The error is the last line of a long log, so only the tail capture can show it.
        Assert.Contains("error CS1002", Message(result), StringComparison.Ordinal);
        Assert.Empty(_provider.Created);
    }

    [Fact]
    public async Task Build_output_in_the_diff_is_refused_before_building()
    {
        var marker = Path.Combine(_git.Dir, "verified");
        _git.Commit("src/a.cs", "class A {}\n");
        _git.Commit("src/App/bin/Debug/App.dll", "binary\n");
        _git.Push();

        var result = await Call(Tool($"touch '{TempWorkflow.YamlPath(marker)}'"));

        Assert.Equal("forbidden_paths", Category(result));
        Assert.Contains("src/App/bin/Debug/App.dll", Message(result), StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.Empty(_provider.Created);
    }

    [Fact]
    public async Task More_files_than_max_files_is_refused()
    {
        _git.Commit("a.txt", "a\n");
        _git.Commit("b.txt", "b\n");
        _git.Push();

        var result = await Call(Tool(maxFiles: 1));

        Assert.Equal("too_many_files", Category(result));
    }

    [Fact]
    public async Task A_commit_that_was_not_pushed_is_refused()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();
        _git.Commit("b.txt", "b\n");

        var result = await Call(Tool());

        Assert.Equal("not_pushed", Category(result));
        Assert.Empty(_provider.Created);
    }

    [Fact]
    public async Task A_branch_that_was_never_pushed_is_refused()
    {
        _git.Commit("a.txt", "a\n");

        var result = await Call(Tool());

        Assert.Equal("not_pushed", Category(result));
    }

    [Fact]
    public async Task Uncommitted_changes_to_tracked_files_are_refused()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();
        File.WriteAllText(Path.Combine(_git.Work, "a.txt"), "changed\n");

        var result = await Call(Tool());

        Assert.Equal("uncommitted_changes", Category(result));
    }

    [Fact]
    public async Task A_build_that_modifies_tracked_files_is_refused()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();

        var result = await Call(Tool("echo regenerated > a.txt"));

        Assert.Equal("uncommitted_changes", Category(result));
        Assert.Contains("after the verification command ran", Message(result), StringComparison.Ordinal);
        Assert.Empty(_provider.Created);
    }

    [Fact]
    public async Task Untracked_build_output_in_the_workspace_is_fine()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();

        var result = await Call(Tool("mkdir -p bin && echo x > bin/out.dll"));

        Assert.True(result.Success, result.Output?.ToJsonString());
    }

    [Fact]
    public async Task No_changes_against_the_target_is_refused()
    {
        _git.Push();

        var result = await Call(Tool());

        Assert.Equal("no_changes", Category(result));
    }

    [Fact]
    public async Task Working_on_the_target_branch_is_refused()
    {
        GitSandbox.Git(_git.Work, "checkout", "--quiet", "main");

        var result = await Call(Tool());

        Assert.Equal("invalid_branch", Category(result));
    }

    [Fact]
    public async Task An_open_pull_request_is_returned_instead_of_a_new_one()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();
        _provider.Existing = new PullRequestInfo("3", "https://example.test/pr/3", IsDraft: true);

        var result = await Call(Tool());

        Assert.True(result.Success);
        Assert.False(result.Output!["created"]!.GetValue<bool>());
        Assert.Equal("3", result.Output["id"]!.GetValue<string>());
        Assert.Empty(_provider.Created);
    }

    [Fact]
    public async Task A_long_description_is_cut_but_keeps_the_verification_footer()
    {
        _git.Commit("a.txt", "a\n");
        _git.Push();
        _provider.MaxDescriptionLength = 600;

        var result = await Call(Tool(), description: new string('x', 5000));

        Assert.True(result.Success);
        var description = Assert.Single(_provider.Created).Description;
        Assert.True(description.Length <= 600, $"length {description.Length}");
        Assert.Contains("Verified by Symphony", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_title_is_an_argument_error()
    {
        var result = await Call(Tool(), title: "  ");

        Assert.Equal("invalid_arguments", Category(result));
    }

    [Fact]
    public void Spec_timeout_covers_the_verification_command()
    {
        var spec = Assert.Single(Tool().Specs);
        Assert.Equal(60000 + PullRequestTool.OverheadMs, spec.TimeoutMs);
        Assert.True(spec.Mutates);
    }

    [Theory]
    [InlineData("src/App/bin/Debug/App.dll", "bin/")]
    [InlineData("REYMA.Common/OBJ/x.cache", "obj/")]
    [InlineData("packages/EPPlus.4.5.3.3/lib/EPPlus.dll", "packages/")]
    [InlineData("src\\App\\bin\\x.dll", "bin/")]
    [InlineData("lib/thing.nupkg", "*.nupkg")]
    [InlineData("config/secrets.json", "secrets.json")]
    [InlineData("src/binder/Binder.cs", null)]
    [InlineData("src/robin/x.cs", null)]
    [InlineData("docs/packages.md", null)]
    public void Forbidden_patterns_match_directories_at_any_depth(string path, string? expected)
    {
        Assert.Equal(expected, PullRequestTool.ForbiddenPattern(path, ["bin/", "obj/", "packages/", "*.nupkg", "secrets.json"]));
    }

    public void Dispose() => _git.Dispose();
}

public class PullRequestConfigTests
{
    private sealed class FakeFactory : IPullRequestProviderFactory
    {
        public string Kind => "fake";

        public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
        {
            if (provider.ContainsKey("fail")) throw new PullRequestException(PullRequestErrorCategories.InvalidConfig, "fake rejected");
        }

        public IPullRequestProvider Create(PullRequestSettings settings, Func<string, string?> env) => new FakePullRequestProvider();
    }

    private static ServiceConfig Build(string pullRequestYaml, string claudeYaml = "") =>
        new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null, [new FakeFactory()])
            .Build(WorkflowLoader.Parse($"---\ntracker: {{kind: fake}}\n{claudeYaml}{pullRequestYaml}\n---\nbody"), "WORKFLOW.md");

    [Fact]
    public void No_section_means_no_pull_request_settings()
    {
        Assert.Null(Build("").PullRequest);
    }

    [Fact]
    public void Defaults_are_draft_origin_and_the_build_output_folders()
    {
        var pr = Build("pull_request:\n  kind: fake\n  target_branch: master\n  verify_command: ./build.sh").PullRequest!;

        Assert.Equal(("fake", "origin", "master", true, "./build.sh"), (pr.Kind, pr.Remote, pr.TargetBranch, pr.Draft, pr.VerifyCommand));
        Assert.Equal(900000, pr.VerifyTimeoutMs);
        Assert.Equal(200, pr.MaxFiles);
        Assert.Equal(ConfigBuilder.DefaultForbiddenPaths, pr.ForbiddenPaths);
    }

    [Theory]
    [InlineData("pull_request:\n  target_branch: master\n  verify_command: x", "pull_request.kind")]
    [InlineData("pull_request:\n  kind: github\n  target_branch: master\n  verify_command: x", "not supported")]
    [InlineData("pull_request:\n  kind: fake\n  target_branch: master", "pull_request.verify_command is required")]
    [InlineData("pull_request:\n  kind: fake\n  verify_command: x", "pull_request.target_branch")]
    [InlineData("pull_request:\n  kind: fake\n  target_branch: master\n  verify_command: x\n  provider: {fail: true}", "fake rejected")]
    public void Invalid_sections_fail_at_load(string yaml, string expected)
    {
        var ex = Assert.Throws<WorkflowException>(() => Build(yaml));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verify_timeout_near_the_stall_timeout_is_a_warning()
    {
        var config = Build(
            "pull_request:\n  kind: fake\n  target_branch: master\n  verify_command: x\n  verify_timeout_ms: 1200000",
            "claude:\n  stall_timeout_ms: 1200000\n");

        Assert.Contains(config.Warnings, w => w.Contains("claude.stall_timeout_ms", StringComparison.Ordinal));
    }
}

public class ToolAugmentedTrackerTests
{
    private sealed class FakeToolSet : IAgentToolSet
    {
        public bool Disposed;
        public IReadOnlyList<AgentToolSpec> Specs { get; init; } = [new("create_pull_request", "pr", new JsonObject { ["type"] = "object" }, Mutates: true)];
        public IReadOnlyList<string> SecretEnvironmentNames => ["FAKE_PR_TOKEN"];

        public Task<AgentToolResult> ExecuteAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct) =>
            Task.FromResult(new AgentToolResult(true, new JsonObject { ["from"] = "toolset" }));

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Routes_workspace_tools_to_the_tool_set_and_the_rest_to_the_tracker()
    {
        var tracker = new FakeTracker();
        var extra = new FakeToolSet();
        using (var combined = new ToolAugmentedTracker(tracker, extra))
        {
            Assert.Contains(combined.AgentToolSpecs(), s => s.Name == "echo");
            Assert.Contains(combined.AgentToolSpecs(), s => s.Name == "create_pull_request");
            Assert.Equal(["FAKE_TRACKER_TOKEN", "FAKE_PR_TOKEN"], combined.SecretEnvironmentNames());

            var pr = await combined.ExecuteAgentToolAsync("create_pull_request", new JsonObject(), TestIssues.Make("1"), default);
            Assert.Equal("toolset", pr.Output!["from"]!.GetValue<string>());
            await combined.ExecuteAgentToolAsync("echo", new JsonObject(), TestIssues.Make("1"), default);
            Assert.Single(tracker.ToolCalls);
        }

        // The session owns the tool set; the tracker belongs to the workflow and must survive it.
        Assert.True(extra.Disposed);
        Assert.Equal(0, tracker.Disposals);
    }

    [Fact]
    public void A_tool_set_may_not_shadow_a_tracker_tool()
    {
        var clash = new FakeToolSet { Specs = [new("echo", "x", new JsonObject(), Mutates: false)] };
        Assert.Throws<InvalidOperationException>(() => new ToolAugmentedTracker(new FakeTracker(), clash));
    }
}

public class AzureReposPullRequestProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly Dictionary<string, object?> Provider = new()
    {
        ["organization_url"] = "https://dev.azure.com/Contoso",
        ["project"] = "My Project",
        ["repository"] = "dev",
        ["token"] = "$TEST_PAT",
    };

    private static AzureReposPullRequestProvider Make(Handler handler) =>
        new(AzureReposOptions.Parse(Provider, n => n == "TEST_PAT" ? "secret-pat" : null), handler);

    [Fact]
    public async Task Creates_a_draft_and_returns_the_web_url()
    {
        var handler = new Handler(_ => Json("""{"pullRequestId": 42, "isDraft": true, "url": "https://dev.azure.com/api/x"}""", HttpStatusCode.Created));
        using var provider = Make(handler);

        var pr = await provider.CreateAsync(new PullRequestDraft("wr/WR-1", "master", "T", "D", IsDraft: true), default);

        Assert.Equal(("42", true), (pr.Id, pr.IsDraft));
        Assert.Equal("https://dev.azure.com/Contoso/My%20Project/_git/dev/pullrequest/42", pr.Url);
        var (method, uri, body, auth) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.StartsWith("https://dev.azure.com/Contoso/My%20Project/_apis/git/repositories/dev/pullrequests?api-version=7.1", uri, StringComparison.Ordinal);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("refs/heads/wr/WR-1", json["sourceRefName"]!.GetValue<string>());
        Assert.Equal("refs/heads/master", json["targetRefName"]!.GetValue<string>());
        Assert.True(json["isDraft"]!.GetValue<bool>());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":secret-pat")), auth);
    }

    [Fact]
    public async Task Finds_the_active_pull_request_for_the_branch()
    {
        var handler = new Handler(_ => Json("""{"value": [{"pullRequestId": 9, "isDraft": false}], "count": 1}"""));
        using var provider = Make(handler);

        var pr = await provider.FindActiveAsync("wr/WR-1", "master", default);

        Assert.Equal("9", pr!.Id);
        var uri = Assert.Single(handler.Requests).Uri;
        Assert.Contains("searchCriteria.sourceRefName=refs%2Fheads%2Fwr%2FWR-1", uri, StringComparison.Ordinal);
        Assert.Contains("searchCriteria.status=active", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_active_pull_request_is_null()
    {
        using var provider = Make(new Handler(_ => Json("""{"value": [], "count": 0}""")));
        Assert.Null(await provider.FindActiveAsync("wr/WR-1", "master", default));
    }

    [Fact]
    public async Task An_html_203_is_an_auth_error_that_never_shows_the_token()
    {
        using var provider = Make(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
        {
            Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html"),
        }));

        var ex = await Assert.ThrowsAsync<PullRequestException>(() => provider.FindActiveAsync("a", "b", default));
        Assert.Equal(PullRequestErrorCategories.Status, ex.Category);
        Assert.Contains("Code (Read & Write)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-pat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflict_carries_the_provider_message()
    {
        using var provider = Make(new Handler(_ => Json("""{"message": "An active pull request already exists."}""", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<PullRequestException>(() => provider.CreateAsync(new PullRequestDraft("a", "b", "t", "d", true), default));
        Assert.Contains("409: An active pull request already exists.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_token_and_repository_are_config_errors()
    {
        var noToken = new Dictionary<string, object?>(Provider) { ["token"] = "$NOPE" };
        Assert.Equal(PullRequestErrorCategories.MissingSecret,
            Assert.Throws<PullRequestException>(() => AzureReposOptions.Parse(noToken, _ => null)).Category);

        var noRepo = new Dictionary<string, object?>(Provider);
        noRepo.Remove("repository");
        Assert.Equal(PullRequestErrorCategories.InvalidConfig,
            Assert.Throws<PullRequestException>(() => AzureReposOptions.Parse(noRepo, _ => "x")).Category);
    }

    [Fact]
    public void The_token_variable_is_stripped_from_agents()
    {
        using var provider = Make(new Handler(_ => Json("{}")));
        Assert.Contains("TEST_PAT", provider.SecretEnvironmentNames);
        Assert.Contains("AZURE_DEVOPS_PAT", provider.SecretEnvironmentNames);
    }
}

public class ShellRunnerTailTests
{
    [Fact]
    public async Task Keep_tail_returns_the_last_part_of_a_long_output()
    {
        var cwd = Path.GetTempPath();
        var result = await ShellRunner.RunAsync("for i in $(seq 1 5000); do echo line $i; done", cwd, 60000, default, keepTail: true);

        Assert.True(result.Succeeded);
        Assert.StartsWith("[truncated]…", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("line 5000", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length <= ShellRunner.MaxCapturedChars + 20);
    }
}
