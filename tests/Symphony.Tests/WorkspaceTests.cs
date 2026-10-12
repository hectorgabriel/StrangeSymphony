using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Config;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public class WorkspaceKeyTests
{
    [Theory]
    [InlineData("ABC-123")]
    [InlineData("WI-42")]
    [InlineData("a.b_c-d")]
    public void Clean_identifiers_are_unchanged(string id) => Assert.Equal(id, WorkspaceKeys.For(id));

    [Fact]
    public void Sanitized_identifiers_get_a_stable_64_bit_hash_suffix()
    {
        var key = WorkspaceKeys.For("AB#12");
        Assert.Matches("^AB_12-[0-9a-f]{16}$", key);
        Assert.Equal(key, WorkspaceKeys.For("AB#12"));
    }

    [Fact]
    public void Distinct_identifiers_that_sanitize_alike_get_distinct_keys()
    {
        var keys = new[] { "AB#12", "AB/12", "AB 12", "AB_12" }.Select(WorkspaceKeys.For).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^[A-Za-z0-9._-]+$", k));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../../etc")]
    public void Path_tricks_never_escape(string id)
    {
        var key = WorkspaceKeys.For(id);
        Assert.NotEqual("..", key);
        Assert.NotEqual(".", key);
        Assert.DoesNotContain("/", key);
    }
}

public class WorkspaceManagerTests : IDisposable
{
    private readonly TempWorkflow _tmp = new("tracker: {kind: fake}");
    private readonly WorkspaceManager _manager = new(NullLogger<WorkspaceManager>.Instance);

    private ServiceConfig Config(string hooks = "")
    {
        _tmp.Write($"tracker: {{kind: fake}}\nworkspace: {{root: \"{{ROOT}}\"}}\n{hooks}");
        return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(_tmp.Path), _tmp.Path);
    }

    [Fact]
    public async Task Creates_then_reuses_and_runs_after_create_once()
    {
        var config = Config("hooks:\n  after_create: echo created >> marker.txt");
        var first = await _manager.CreateForIssueAsync("WI-1", config, default);
        var second = await _manager.CreateForIssueAsync("WI-1", config, default);

        Assert.True(first.CreatedNow);
        Assert.False(second.CreatedNow);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(Path.Combine(_tmp.WorkspaceRoot, "WI-1"), first.Path);
        Assert.Single(File.ReadAllLines(Path.Combine(first.Path, "marker.txt")));
    }

    [Fact]
    public async Task After_create_failure_aborts_and_removes_partial_workspace()
    {
        var config = Config("hooks:\n  after_create: exit 3");
        await Assert.ThrowsAsync<WorkspaceException>(() => _manager.CreateForIssueAsync("WI-2", config, default));
        Assert.False(Directory.Exists(Path.Combine(_tmp.WorkspaceRoot, "WI-2")));
    }

    [Fact]
    public async Task Hook_timeout_is_enforced()
    {
        var config = Config("hooks:\n  timeout_ms: 200\n  after_create: sleep 5");
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<WorkspaceException>(() => _manager.CreateForIssueAsync("WI-3", config, default));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Existing_file_at_workspace_path_fails_safely()
    {
        var config = Config();
        Directory.CreateDirectory(_tmp.WorkspaceRoot);
        await File.WriteAllTextAsync(Path.Combine(_tmp.WorkspaceRoot, "WI-4"), "not a dir");
        await Assert.ThrowsAsync<WorkspaceException>(() => _manager.CreateForIssueAsync("WI-4", config, default));
        Assert.True(File.Exists(Path.Combine(_tmp.WorkspaceRoot, "WI-4")));
    }

    [Fact]
    public async Task Remove_runs_before_remove_and_ignores_its_failure()
    {
        var outside = Path.Combine(_tmp.Dir, "removed.txt");
        var config = Config($"hooks:\n  before_remove: \"echo bye > '{outside}'; exit 1\"");
        var ws = await _manager.CreateForIssueAsync("WI-5", config, default);
        await _manager.RemoveAsync("WI-5", config, default);
        Assert.False(Directory.Exists(ws.Path));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Hooks_run_with_workspace_as_cwd()
    {
        var config = Config("hooks:\n  after_create: pwd > cwd.txt");
        var ws = await _manager.CreateForIssueAsync("WI-6", config, default);
        var cwd = (await File.ReadAllTextAsync(Path.Combine(ws.Path, "cwd.txt"))).Trim();
        Assert.Equal(new DirectoryInfo(ws.Path).FullName.Replace("/private/", "/"), cwd.Replace("/private/", "/"));
    }

    [Fact]
    public void Root_containment_rejects_outside_paths()
    {
        Assert.Throws<WorkspaceException>(() => WorkspaceManager.EnsureInsideRoot("/tmp/root", "/tmp/root"));
        Assert.Throws<WorkspaceException>(() => WorkspaceManager.EnsureInsideRoot("/tmp/root", "/tmp/rootevil/x"));
        Assert.Throws<WorkspaceException>(() => WorkspaceManager.EnsureInsideRoot("/tmp/root", "/tmp/root/../x"));
        WorkspaceManager.EnsureInsideRoot("/tmp/root", "/tmp/root/WI-1");
    }

    public void Dispose() => _tmp.Dispose();
}
