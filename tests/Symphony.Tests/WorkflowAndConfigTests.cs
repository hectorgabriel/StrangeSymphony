using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Config;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

public class WorkflowLoaderTests
{
    [Fact]
    public void Splits_front_matter_and_trims_body()
    {
        var wf = WorkflowLoader.Parse("---\ntracker:\n  kind: fake\npolling:\n  interval_ms: 500\n---\n\n  Hello {{ issue.title }}  \n\n");
        Assert.Equal("Hello {{ issue.title }}", wf.PromptTemplate);
        var tracker = Assert.IsType<Dictionary<string, object?>>(wf.Config["tracker"]);
        Assert.Equal("fake", tracker["kind"]);
        Assert.Equal(500L, ((Dictionary<string, object?>)wf.Config["polling"]!)["interval_ms"]);
    }

    [Fact]
    public void No_front_matter_means_whole_file_is_prompt()
    {
        var wf = WorkflowLoader.Parse("Just a prompt\n");
        Assert.Empty(wf.Config);
        Assert.Equal("Just a prompt", wf.PromptTemplate);
    }

    [Fact]
    public void Block_scalars_stay_strings()
    {
        var wf = WorkflowLoader.Parse("---\nhooks:\n  after_create: |\n    git clone x .\n    echo 1\n---\nbody");
        var hooks = (Dictionary<string, object?>)wf.Config["hooks"]!;
        Assert.Equal("git clone x .\necho 1", ((string)hooks["after_create"]!).TrimEnd());
    }

    [Theory]
    [InlineData("---\n- a\n- b\n---\nbody", WorkflowErrorCodes.FrontMatterNotAMap)]
    [InlineData("---\njust a string\n---\nbody", WorkflowErrorCodes.FrontMatterNotAMap)]
    [InlineData("---\ntracker: [unclosed\n---\nbody", WorkflowErrorCodes.WorkflowParseError)]
    [InlineData("---\ntracker:\n  kind: x\n", WorkflowErrorCodes.WorkflowParseError)]
    public void Invalid_front_matter_returns_typed_errors(string text, string code)
    {
        var ex = Assert.Throws<WorkflowException>(() => WorkflowLoader.Parse(text));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Missing_file_is_typed_error()
    {
        var ex = Assert.Throws<WorkflowException>(() => WorkflowLoader.Load("/definitely/not/here/WORKFLOW.md"));
        Assert.Equal(WorkflowErrorCodes.MissingWorkflowFile, ex.Code);
    }
}

public class ConfigBuilderTests
{
    private static readonly Dictionary<string, string> Env = new() { ["WS_ROOT"] = "/tmp/from-env", ["HOMEISH"] = "/opt" };

    private static ConfigBuilder Builder() => new([new FakeTrackerFactory(new FakeTracker())], n => Env.GetValueOrDefault(n));

    private static ServiceConfig Build(string yaml, string path = "/repo/WORKFLOW.md") =>
        Builder().Build(WorkflowLoader.Parse($"---\n{yaml}\n---\nprompt"), path);

    [Fact]
    public void Defaults_apply()
    {
        var c = Build("tracker:\n  kind: fake");
        Assert.Equal(30000, c.PollIntervalMs);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "symphony_workspaces"), c.WorkspaceRoot);
        Assert.Equal(60000, c.Hooks.TimeoutMs);
        Assert.Equal(10, c.Agent.MaxConcurrentAgents);
        Assert.Equal(20, c.Agent.MaxTurns);
        Assert.Equal(300000, c.Agent.MaxRetryBackoffMs);
        Assert.Equal("claude", c.Claude.Command);
        Assert.Equal(3600000, c.Claude.TurnTimeoutMs);
        Assert.Equal(5000, c.Claude.ReadTimeoutMs);
        Assert.Equal(300000, c.Claude.StallTimeoutMs);
        Assert.Equal(["Todo", "In Progress"], c.Tracker.ActiveStates);
        Assert.Equal(["Done", "Cancelled"], c.Tracker.TerminalStates);
        Assert.Null(c.ServerPort);
    }

    [Fact]
    public void Workspace_root_supports_env_tilde_and_relative_paths()
    {
        Assert.Equal("/tmp/from-env/ws", Build("tracker: {kind: fake}\nworkspace:\n  root: $WS_ROOT/ws").WorkspaceRoot);
        Assert.Equal("/opt/x", Build("tracker: {kind: fake}\nworkspace:\n  root: ${HOMEISH}/x").WorkspaceRoot);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sym"),
            Build("tracker: {kind: fake}\nworkspace:\n  root: ~/sym").WorkspaceRoot);
        Assert.Equal("/repo/work", Build("tracker: {kind: fake}\nworkspace:\n  root: ./work").WorkspaceRoot);

        var ex = Assert.Throws<WorkflowException>(() => Build("tracker: {kind: fake}\nworkspace:\n  root: $NOPE/x"));
        Assert.Equal(ConfigErrorCodes.InvalidConfig, ex.Code);
    }

    [Fact]
    public void Claude_command_is_preserved_verbatim()
    {
        var c = Build("tracker: {kind: fake}\nclaude:\n  command: \"$HOME/bin/claude --foo 'bar baz'\"");
        Assert.Equal("$HOME/bin/claude --foo 'bar baz'", c.Claude.Command);
    }

    [Fact]
    public void By_state_limits_normalize_keys_and_ignore_invalid_values()
    {
        var c = Build("tracker: {kind: fake}\nagent:\n  max_concurrent_agents_by_state:\n    \" In Progress \": 2\n    Todo: 0\n    Review: abc\n    QA: \"3\"");
        Assert.Equal(new Dictionary<string, int> { ["in progress"] = 2, ["qa"] = 3 }, c.Agent.MaxConcurrentAgentsByState);
    }

    [Theory]
    [InlineData("tracker: {kind: fake}\nagent:\n  max_turns: 0")]
    [InlineData("tracker: {kind: fake}\nagent:\n  max_turns: lots")]
    [InlineData("tracker: {kind: fake}\nhooks:\n  timeout_ms: -1")]
    [InlineData("tracker: {kind: fake}\nclaude:\n  command: \"  \"")]
    public void Invalid_values_fail_validation(string yaml)
    {
        var ex = Assert.Throws<WorkflowException>(() => Build(yaml));
        Assert.Equal(ConfigErrorCodes.InvalidConfig, ex.Code);
    }

    [Fact]
    public void Tracker_kind_is_required_and_must_be_supported()
    {
        Assert.Equal(ConfigErrorCodes.MissingTrackerKind, Assert.Throws<WorkflowException>(() => Build("polling: {interval_ms: 5}")).Code);
        Assert.Equal(TrackerErrorCategories.UnsupportedTrackerKind, Assert.Throws<WorkflowException>(() => Build("tracker: {kind: jira}")).Code);
        Assert.Equal(TrackerErrorCategories.InvalidTrackerConfig, Assert.Throws<WorkflowException>(() => Build("tracker:\n  kind: fake\n  provider:\n    fail: true")).Code);
    }

    [Fact]
    public void Provider_keys_are_preserved_for_the_adapter()
    {
        var c = Build("tracker:\n  kind: fake\n  provider:\n    custom_key: 1\n    nested: {a: b}");
        Assert.Equal(1L, c.Tracker.Provider["custom_key"]);
        Assert.Equal("b", ((Dictionary<string, object?>)c.Tracker.Provider["nested"]!)["a"]);
    }

    [Fact]
    public void Codex_key_is_ignored_with_warning()
    {
        var c = Build("tracker: {kind: fake}\ncodex:\n  command: codex app-server");
        Assert.Contains(c.Warnings, w => w.Contains("codex"));
    }

    [Fact]
    public void State_comparisons_are_case_insensitive_and_terminal_wins()
    {
        var c = Build("tracker:\n  kind: fake\n  active_states: [Todo, Done]\n  terminal_states: [done]");
        Assert.True(c.IsActiveState(" todo "));
        Assert.False(c.IsActiveState("Done"));
        Assert.True(c.IsTerminalState("DONE"));
    }
}

public class WorkflowStoreTests
{
    [Fact]
    public async Task Reload_applies_changes_and_keeps_last_good_on_error()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}\npolling: {interval_ms: 1000}");
        using var store = tmp.Store(new FakeTracker());
        store.LoadInitial();
        Assert.Equal(1000, store.Current.Config.PollIntervalMs);

        tmp.Write("tracker: {kind: fake}\npolling: {interval_ms: 2000}", "New prompt");
        Assert.True(store.ReloadIfChanged());
        Assert.Equal(2000, store.Current.Config.PollIntervalMs);
        Assert.Equal("New prompt", store.Current.PromptTemplate);

        File.WriteAllText(tmp.Path, "---\ntracker: [broken\n---\nx");
        Assert.False(store.ReloadIfChanged());
        Assert.NotNull(store.LastError);
        Assert.Equal(2000, store.Current.Config.PollIntervalMs);

        tmp.Write("tracker: {kind: fake}\npolling: {interval_ms: 3000}");
        Assert.True(store.ReloadIfChanged());
        Assert.Null(store.LastError);
        Assert.Equal(3000, store.Current.Config.PollIntervalMs);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task File_watch_detects_changes()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}\npolling: {interval_ms: 1000}");
        using var store = tmp.Store(new FakeTracker());
        store.LoadInitial();
        store.StartWatching();
        tmp.Write("tracker: {kind: fake}\npolling: {interval_ms: 4321}");
        await Wait.Until(() => store.Current.Config.PollIntervalMs == 4321, "watcher reload");
    }

    [Fact]
    public void Template_syntax_errors_fail_load()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}", "{% if %}");
        using var store = tmp.Store(new FakeTracker());
        Assert.Equal(WorkflowErrorCodes.TemplateParseError, Assert.Throws<WorkflowException>(() => store.LoadInitial()).Code);
    }

    [Fact]
    public void Startup_with_invalid_config_throws()
    {
        using var tmp = new TempWorkflow("polling: {interval_ms: 1}");
        var store = new WorkflowStore(tmp.Path, new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null), _ => null, NullLoggerFactory.Instance);
        Assert.Throws<WorkflowException>(() => store.LoadInitial());
    }

    [Fact]
    public void Reload_disposes_the_replaced_adapter_once_its_last_lease_is_released()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var factory = new PerSnapshotTrackerFactory();
        var store = new WorkflowStore(tmp.Path, new ConfigBuilder([factory], _ => null), _ => null, NullLoggerFactory.Instance);
        store.LoadInitial();
        var lease = store.Acquire();

        tmp.Write("tracker: {kind: fake}", "changed body");
        Assert.True(store.ReloadIfChanged());
        var (first, second) = (factory.Created[0], factory.Created[1]);
        Assert.Same(second, store.Current.Tracker);
        Assert.Equal(0, first.Disposals); // still leased by a running worker

        using (store.Acquire(lease.Workflow))
        {
            lease.Dispose();
            lease.Dispose(); // idempotent
            Assert.Equal(0, first.Disposals);
        }

        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);

        store.Dispose();
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public void Reload_keeps_an_adapter_shared_with_the_new_snapshot()
    {
        using var tmp = new TempWorkflow("tracker: {kind: fake}");
        var tracker = new FakeTracker();
        using var store = tmp.Store(tracker);
        store.LoadInitial();
        tmp.Write("tracker: {kind: fake}", "changed body");
        Assert.True(store.ReloadIfChanged());
        Assert.Equal(0, tracker.Disposals);
    }
}
