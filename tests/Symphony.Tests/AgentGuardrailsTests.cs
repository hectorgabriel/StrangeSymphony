using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Orchestration;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public sealed class AgentGuardrailsTests : IDisposable
{
    private readonly TempWorkflow _tmp = new("tracker: {kind: fake}");

    public void Dispose() => _tmp.Dispose();

    private ServiceConfig Build(string extra)
    {
        _tmp.Write($"tracker: {{kind: fake}}\n{extra}");
        return new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null).Build(WorkflowLoader.Load(_tmp.Path), _tmp.Path);
    }

    // ---- Config ------------------------------------------------------------------------------

    [Fact]
    public void Defaults_park_after_two_idle_turns_and_allow_any_state_that_is_not_terminal()
    {
        var config = Build("");
        Assert.Equal(2, config.Agent.MaxIdleTurns);
        Assert.Null(config.Agent.AllowedStates);
        Assert.True(config.Agent.MayMoveTo("Review", config.Tracker.TerminalStates));
        Assert.False(config.Agent.MayMoveTo(" done ", config.Tracker.TerminalStates));
    }

    [Fact]
    public void Allowed_states_are_the_only_targets_and_may_include_a_terminal_one()
    {
        var config = Build("agent:\n  max_idle_turns: 0\n  allowed_states: [Review, Done]");
        Assert.Equal(0, config.Agent.MaxIdleTurns);
        Assert.True(config.Agent.MayMoveTo("review", config.Tracker.TerminalStates));
        Assert.True(config.Agent.MayMoveTo("Done", config.Tracker.TerminalStates));
        Assert.False(config.Agent.MayMoveTo("In Progress", config.Tracker.TerminalStates));
    }

    [Fact]
    public void An_empty_allowed_states_list_is_a_config_error()
    {
        var ex = Assert.Throws<WorkflowException>(() => Build("agent:\n  allowed_states: []"));
        Assert.Contains("agent.allowed_states", ex.Message);
    }

    [Fact]
    public void A_gate_or_send_back_state_agents_may_not_reach_gets_a_warning()
    {
        var config = Build("agent:\n  allowed_states: [Review]\ngates:\n  QA:\n    run: [\"true\"]\ndefects:\n  states: [Rework]\n  categories: [ui]");
        Assert.Contains(config.Warnings, w => w.StartsWith("gates.QA: agent.allowed_states (Review) does not include \"QA\"", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.StartsWith("defects.states: agent.allowed_states (Review) does not include \"rework\"", StringComparison.Ordinal));
    }

    // ---- State guard -------------------------------------------------------------------------

    [Fact]
    public async Task The_guard_refuses_a_terminal_state_without_asking_the_tracker()
    {
        var inner = new StateTracker();
        var guard = new StateGuardTracker(inner, Build(""));

        var result = await guard.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "Done" }, TestIssues.Make("1"), default);

        Assert.False(result.Success);
        Assert.Equal(StateGuardTracker.RefusedCategory, (string?)result.Output!["error"]!["category"]);
        Assert.Contains("Don't try other states", (string?)result.Output!["error"]!["message"]);
        Assert.Empty(inner.Moves);
    }

    [Fact]
    public async Task With_allowed_states_only_those_reach_the_tracker()
    {
        var inner = new StateTracker();
        var guard = new StateGuardTracker(inner, Build("agent:\n  allowed_states: [Review]"));

        var refused = await guard.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "In Progress" }, TestIssues.Make("1"), default);
        var moved = await guard.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "review" }, TestIssues.Make("1"), default);

        Assert.False(refused.Success);
        Assert.True(moved.Success);
        Assert.Equal(["review"], inner.Moves);
        Assert.Contains("only lets you move the issue to \"Review\"", Assert.Single(guard.AgentToolSpecs(), s => s.Name == "update_state").Description);
    }

    [Fact]
    public async Task A_state_the_tracker_rejects_tells_the_agent_to_stop_trying_others()
    {
        var guard = new StateGuardTracker(new StateTracker(), Build(""));

        var result = await guard.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "Resolved" }, TestIssues.Make("1"), default);

        var message = (string?)result.Output!["error"]!["message"];
        Assert.StartsWith("Azure DevOps returned 400", message);
        Assert.Contains("Don't try other states", message);
    }

    [Fact]
    public async Task Other_tools_pass_through_the_guard()
    {
        var inner = new StateTracker();
        var guard = new StateGuardTracker(inner, Build(""));
        var result = await guard.ExecuteAgentToolAsync("add_comment", new JsonObject { ["text"] = "hi" }, TestIssues.Make("1"), default);
        Assert.True(result.Success);
        Assert.Equal(["add_comment"], inner.Calls);
    }

    // ---- Startup state check -----------------------------------------------------------------

    [Fact]
    public void The_state_check_names_each_type_that_lacks_a_gate_or_allowed_state()
    {
        var config = Build("agent:\n  allowed_states: [Resolved]\ngates:\n  Resolved:\n    run: [\"true\"]");
        var warnings = WorkflowStateCheck.Warnings(config,
        [
            new WorkItemTypeStates("User Story", ["New", "Active", "Resolved", "Closed", "Removed"]),
            new WorkItemTypeStates("Task", ["New", "Active", "Closed", "Removed"]),
        ]);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("work item type \"Task\" has no state \"Resolved\" (its states: New, Active, Closed, Removed)", warning);
        Assert.Contains("the gate on \"Resolved\" and \"Resolved\" in agent.allowed_states", warning);
    }

    // ---- Progress ----------------------------------------------------------------------------

    [Fact]
    public async Task Only_successful_changes_other_than_comments_count_as_tracker_progress()
    {
        var counter = new ProgressCountingTracker(new StateTracker());
        var issue = TestIssues.Make("1");
        await counter.ExecuteAgentToolAsync("get_issue", [], issue, default);
        await counter.ExecuteAgentToolAsync("add_comment", new JsonObject { ["text"] = "still blocked" }, issue, default);
        await counter.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "Resolved" }, issue, default);
        Assert.Equal(0, counter.Changes);

        await counter.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = "Review" }, issue, default);
        Assert.Equal(1, counter.Changes);
    }

    [Fact]
    public async Task The_workspace_probe_sees_commits_new_files_and_further_edits()
    {
        using var git = new GitSandbox();
        git.Commit("a.txt", "one\n");
        var start = await ProgressProbe.WorkspaceAsync(git.Work, default);
        Assert.NotNull(start);
        Assert.Equal(start, await ProgressProbe.WorkspaceAsync(git.Work, default));

        File.WriteAllText(Path.Combine(git.Work, "a.txt"), "two\n");
        var edited = await ProgressProbe.WorkspaceAsync(git.Work, default);
        File.WriteAllText(Path.Combine(git.Work, "a.txt"), "three\n");
        var editedAgain = await ProgressProbe.WorkspaceAsync(git.Work, default);
        File.WriteAllText(Path.Combine(git.Work, "b.txt"), "new\n");
        var added = await ProgressProbe.WorkspaceAsync(git.Work, default);

        Assert.Equal(4, new[] { start, edited, editedAgain, added }.Distinct().Count());
        Assert.Null(await ProgressProbe.WorkspaceAsync(Directory.CreateTempSubdirectory("symphony-nogit-").FullName, default));
    }

    // ---- Worker ------------------------------------------------------------------------------

    private const string GitWorkspace =
        "hooks:\n  after_create: \"git init -q && git -c user.email=t@t -c user.name=t commit -q --allow-empty -m init\"\n";

    private async Task<(List<string> Prompts, List<AgentEvent> Events)> RunWorkerAsync(string extra, Action<string, int>? onTurn = null)
    {
        _tmp.Write($"tracker: {{kind: fake}}\nworkspace: {{root: \"{{ROOT}}\"}}\n{GitWorkspace}{extra}");
        var tracker = new FakeTracker();
        var wf = _tmp.Store(tracker).LoadInitial();
        var issue = TestIssues.Make("1");
        tracker.Put(issue);
        var prompts = new List<string>();
        var events = new List<AgentEvent>();
        var factory = new TurnFactory(prompts, onTurn);
        await new AgentWorker(new WorkspaceManager(NullLogger<WorkspaceManager>.Instance), factory, NullLogger<AgentWorker>.Instance)
            .RunAsync(issue, null, wf, events.Add, _ => { }, default);
        return (prompts, events);
    }

    [Fact]
    public async Task A_session_that_changes_nothing_ends_after_max_idle_turns()
    {
        var (prompts, events) = await RunWorkerAsync("agent:\n  max_turns: 20");

        Assert.Equal(2, prompts.Count);
        Assert.Contains(events, e => e.Event == AgentEventNames.SessionIdle);
    }

    [Fact]
    public async Task A_session_that_keeps_changing_the_workspace_runs_to_max_turns()
    {
        var (prompts, events) = await RunWorkerAsync("agent:\n  max_turns: 4",
            (cwd, turn) => File.WriteAllText(Path.Combine(cwd, "notes.txt"), new string('x', turn)));

        Assert.Equal(4, prompts.Count);
        Assert.DoesNotContain(events, e => e.Event == AgentEventNames.SessionIdle);
    }

    [Fact]
    public async Task Max_idle_turns_zero_turns_the_check_off()
    {
        var (prompts, events) = await RunWorkerAsync("agent:\n  max_turns: 3\n  max_idle_turns: 0");

        Assert.Equal(3, prompts.Count);
        Assert.DoesNotContain(events, e => e.Event == AgentEventNames.SessionIdle);
    }

    // ---- Orchestrator: waiting for input -----------------------------------------------------

    private static readonly DateTimeOffset Changed = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task ParkFirstSessionAsync(OrchestratorHarness h)
    {
        h.Tracker.Put(TestIssues.Make("1") with { UpdatedAt = Changed });
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        SessionResumeTests.Session(h.Workers[0], "sess-1");
        h.Workers[0].Emit(new AgentEvent(AgentEventNames.SessionIdle, DateTimeOffset.UtcNow) { Message = "no progress in 2 turn(s)" });
        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Running.Count == 0, "exit");
        await Wait.Until(() => { lock (h.Tracker.ToolCalls) return h.Tracker.ToolCalls.Count == 1; }, "comment");
    }

    [Fact]
    public async Task An_idle_session_parks_the_issue_until_someone_changes_it()
    {
        await using var h = new OrchestratorHarness();
        await ParkFirstSessionAsync(h);

        var (tool, args, _) = h.Tracker.ToolCalls[0];
        Assert.Equal("add_comment", tool);
        Assert.Contains("waiting for an answer", (string?)args["text"]);
        Assert.Empty(h.Snap.Retrying);
        Assert.Contains($"\"outcome\":\"{RunOutcomes.Waiting}\"", File.ReadAllText(MetricsLog.PathFor(h.Tmp.WorkspaceRoot)), StringComparison.Ordinal);

        // Still active and unchanged: not dispatched, and the board says why.
        await h.Tick();
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.Tick();
        Assert.Single(h.Workers);
        Assert.Equal("awaiting_input", Assert.Single(h.Snap.Queue).Status);

        // A reply changes the work item: the session that asked resumes.
        h.Tracker.Put(TestIssues.Make("1") with { UpdatedAt = Changed.AddMinutes(5) });
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "woken");
        Assert.Equal("sess-1", h.Workers[1].Resume);
    }

    [Fact]
    public async Task A_parked_issue_stays_parked_across_a_restart()
    {
        await using var h = new OrchestratorHarness();
        await ParkFirstSessionAsync(h);
        await h.Tick();

        await h.RestartAsync();
        await h.Tick();
        await h.Tick();
        Assert.Single(h.Workers);

        h.Tracker.Put(TestIssues.Make("1", state: "In Progress") with { UpdatedAt = Changed });
        await h.Tick();
        await Wait.Until(() => h.Workers.Length == 2, "woken by a state change");
    }

    // ---- Fakes -------------------------------------------------------------------------------

    /// <summary>update_state succeeds except for "Resolved", which it rejects like Azure DevOps does for a Task.</summary>
    private sealed class StateTracker : ITrackerAdapter
    {
        public List<string> Moves { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);

        public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
        [
            new("get_issue", "read", new JsonObject { ["type"] = "object" }, Mutates: false),
            new("add_comment", "comment", new JsonObject { ["type"] = "object" }, Mutates: true),
            new("update_state", "move", new JsonObject { ["type"] = "object" }, Mutates: true),
        ];

        public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
        {
            Calls.Add(name);
            if (name != "update_state")
            {
                return Task.FromResult(new AgentToolResult(true, new JsonObject()));
            }

            var state = (string)arguments["state"]!;
            if (state == "Resolved")
            {
                return Task.FromResult(AgentToolResult.Fail(TrackerErrorCategories.Status,
                    "Azure DevOps returned 400: The field 'State' contains the value 'Resolved' that is not in the list of supported values"));
            }

            Moves.Add(state);
            return Task.FromResult(new AgentToolResult(true, new JsonObject { ["state"] = state }));
        }

        public void Dispose()
        {
        }
    }

    private sealed class TurnFactory(List<string> prompts, Action<string, int>? onTurn) : IAgentSessionFactory
    {
        public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(new Session(prompts, onTurn, request.WorkspacePath));

        private sealed class Session(List<string> prompts, Action<string, int>? onTurn, string cwd) : IAgentSession
        {
            public string? ThreadId => "thread";

            public Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct)
            {
                prompts.Add(prompt);
                onTurn?.Invoke(cwd, prompts.Count);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
