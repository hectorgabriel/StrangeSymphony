using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Gates;
using Symphony.Core.Tracker;
using Symphony.Core.Util;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>A tracker whose only tool is <c>update_state</c>; it records the states it was asked for.</summary>
internal sealed class StateTracker : ITrackerAdapter
{
    public List<string> Moves { get; } = [];
    public IReadOnlyList<string> Secrets { get; init; } = [];

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Issue>>([]);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Issue>>([]);

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
    [
        new("update_state", "Move the task.", new JsonObject { ["type"] = "object" }, Mutates: true),
        new("add_comment", "Comment.", new JsonObject { ["type"] = "object" }, Mutates: true),
    ];

    public IReadOnlyList<string> SecretEnvironmentNames() => Secrets;

    public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        var state = arguments["state"]?.GetValue<string>() ?? "";
        if (name == "update_state") Moves.Add(state);
        return Task.FromResult(new AgentToolResult(true, new JsonObject { ["state"] = state }));
    }

    public void Dispose()
    {
    }
}

public sealed class VerificationGateTests : IDisposable
{
    private readonly GitSandbox _git = new();

    private static GateSettings Gate(string state = "Por Probar", bool requirePushed = true, int timeoutMs = 60000, params string[] run) =>
        new(state, run, requirePushed, "origin", timeoutMs);

    private Task<GateOutcome> Check(GateSettings gate, params string[] strip) =>
        VerificationGate.CheckAsync(gate, _git.Work, ShellRunner.Auto, strip, default);

    private void CommitAndPush()
    {
        _git.Commit("src/a.cs", "class A {}\n");
        _git.Push();
    }

    [Fact]
    public async Task Passes_when_the_branch_is_pushed_and_every_command_succeeds()
    {
        CommitAndPush();

        var outcome = await Check(Gate(run: ["exit 0", "true"]));

        Assert.True(outcome.Passed, outcome.Message);
        Assert.Equal(_git.Head(), outcome.Commit);
        Assert.Equal("feature", outcome.Branch);
    }

    [Fact]
    public async Task An_unpushed_branch_fails_before_any_command_runs()
    {
        _git.Commit("src/a.cs", "class A {}\n");
        var marker = Path.Combine(_git.Dir, "ran");

        var outcome = await Check(Gate(run: [$"touch '{TempWorkflow.YamlPath(marker)}'"]));

        Assert.False(outcome.Passed);
        Assert.Equal(GateOutcome.NotPushed, outcome.Category);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_commit_after_the_push_fails_as_not_pushed()
    {
        CommitAndPush();
        _git.Commit("src/b.cs", "class B {}\n");

        var outcome = await Check(Gate());

        Assert.Equal(GateOutcome.NotPushed, outcome.Category);
        Assert.Contains("but HEAD is", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untracked_file_counts_as_uncommitted_work()
    {
        CommitAndPush();
        File.WriteAllText(Path.Combine(_git.Work, "analisis.md"), "# Alcance\n");

        var outcome = await Check(Gate());

        Assert.Equal(GateOutcome.UncommittedChanges, outcome.Category);
        Assert.Contains("analisis.md", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_detached_head_fails()
    {
        CommitAndPush();
        GitSandbox.Git(_git.Work, "checkout", "--quiet", "--detach");

        var outcome = await Check(Gate());

        Assert.Equal(GateOutcome.NotOnBranch, outcome.Category);
    }

    [Fact]
    public async Task The_first_failing_command_stops_the_gate_and_its_output_tail_is_returned()
    {
        CommitAndPush();
        var marker = Path.Combine(_git.Dir, "second");

        var outcome = await Check(Gate(run:
        [
            "for i in $(seq 1 3000); do echo line $i; done; echo 'Failed!  - Failed: 2, Passed: 40'; exit 1",
            $"touch '{TempWorkflow.YamlPath(marker)}'",
        ]));

        Assert.Equal(GateOutcome.CommandFailed, outcome.Category);
        Assert.Contains("exited with code 1", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Failed: 2, Passed: 40", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task The_timeout_covers_all_commands_together()
    {
        CommitAndPush();

        var outcome = await Check(Gate(timeoutMs: 1500, run: ["sleep 1", "sleep 5"]));

        Assert.Equal(GateOutcome.CommandFailed, outcome.Category);
        Assert.Contains("did not finish within", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_edits_tracked_files_fails_the_gate()
    {
        CommitAndPush();

        var outcome = await Check(Gate(run: ["echo changed >> src/a.cs"]));

        Assert.Equal(GateOutcome.WorkspaceChanged, outcome.Category);
    }

    [Fact]
    public async Task Without_require_pushed_only_the_commands_run()
    {
        File.WriteAllText(Path.Combine(_git.Work, "scratch.txt"), "x");

        var outcome = await Check(Gate(requirePushed: false, run: ["exit 0"]));

        Assert.True(outcome.Passed, outcome.Message);
    }

    [Fact]
    public async Task Commands_do_not_see_the_tracker_secrets()
    {
        CommitAndPush();
        const string name = "SYMPHONY_GATE_TEST_SECRET";
        Environment.SetEnvironmentVariable(name, "s3cret");
        try
        {
            var outcome = await Check(Gate(run: [$"test -z \"${name}\""]), name);
            Assert.True(outcome.Passed, outcome.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private GatedTracker Gated(StateTracker inner, List<string>? notes = null, params GateSettings[] gates) =>
        new(inner, gates.ToDictionary(g => StateNames.Normalize(g.State)), _git.Work, ShellRunner.Auto, NullLogger.Instance, notes is null ? null : notes.Add);

    private static Task<AgentToolResult> Move(GatedTracker tracker, string state) =>
        tracker.ExecuteAgentToolAsync("update_state", new JsonObject { ["state"] = state }, TestIssues.Make("1", state: "Plan Aprobado"), default);

    [Fact]
    public async Task A_failing_gate_leaves_the_state_unchanged()
    {
        _git.Commit("src/a.cs", "class A {}\n");
        var inner = new StateTracker();
        var notes = new List<string>();

        var result = await Move(Gated(inner, notes, Gate(run: ["exit 0"])), "por probar");

        Assert.False(result.Success);
        Assert.Equal(GateOutcome.NotPushed, result.Output!["error"]!["category"]!.GetValue<string>());
        Assert.Contains("status was not changed", result.Output["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(inner.Moves);
        Assert.Contains(notes, n => n.StartsWith("gate \"Por Probar\" failed: not_pushed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_passing_gate_moves_the_state_and_reports_what_it_checked()
    {
        CommitAndPush();
        var inner = new StateTracker();

        var result = await Move(Gated(inner, null, Gate(run: ["exit 0"])), "Por Probar");

        Assert.True(result.Success, result.Output?.ToJsonString());
        Assert.Equal(["Por Probar"], inner.Moves);
        Assert.Equal(_git.Head(), result.Output!["gate"]!["commit"]!.GetValue<string>());
    }

    [Fact]
    public async Task States_without_a_gate_and_other_tools_go_straight_through()
    {
        _git.Commit("src/a.cs", "class A {}\n"); // not pushed: a gate would fail
        var inner = new StateTracker();
        var tracker = Gated(inner, null, Gate());

        var moved = await Move(tracker, "Requiere Informacion");
        var commented = await tracker.ExecuteAgentToolAsync("add_comment", new JsonObject { ["text"] = "hola" }, TestIssues.Make("1"), default);

        Assert.True(moved.Success);
        Assert.True(commented.Success);
        Assert.Equal(["Requiere Informacion"], inner.Moves);
        Assert.Null(moved.Output!["gate"]);
    }

    [Fact]
    public async Task Gate_commands_lose_the_inner_trackers_secrets()
    {
        CommitAndPush();
        const string name = "SYMPHONY_GATE_TRACKER_SECRET";
        Environment.SetEnvironmentVariable(name, "pat");
        try
        {
            var inner = new StateTracker { Secrets = [name] };
            var result = await Move(Gated(inner, null, Gate(run: [$"test -z \"${name}\""])), "Por Probar");
            Assert.True(result.Success, result.Output?.ToJsonString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void The_update_state_spec_names_the_gated_states_and_allows_for_the_gate()
    {
        var tracker = Gated(new StateTracker(), null, Gate(timeoutMs: 600000, run: ["dotnet test"]), Gate("En Revision", run: ["dotnet test"]));

        var spec = tracker.AgentToolSpecs().Single(s => s.Name == "update_state");

        Assert.Contains("\"Por Probar\", \"En Revision\"", spec.Description, StringComparison.Ordinal);
        // The longest gate sets the limit: 600 s here against the other's 60 s.
        Assert.Equal(600000 + ConfigBuilder.GateOverheadMs, spec.TimeoutMs);
        Assert.Null(tracker.AgentToolSpecs().Single(s => s.Name == "add_comment").TimeoutMs);
    }

    public void Dispose() => _git.Dispose();
}

public class GateConfigTests
{
    private static ServiceConfig Build(string yaml) =>
        new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null)
            .Build(WorkflowLoader.Parse($"---\ntracker: {{kind: fake}}\n{yaml}\n---\nbody"), "WORKFLOW.md");

    [Fact]
    public void No_section_means_no_gates()
    {
        Assert.Empty(Build("").Gates!);
        Assert.Null(Build("").GateFor("Done"));
    }

    [Fact]
    public void Parses_commands_as_a_list_or_one_string_with_defaults()
    {
        var config = Build("""
            gates:
              Por Probar:
                run: [dotnet build, dotnet test --no-build]
              En Revision:
                run: dotnet test
                timeout_ms: 300000
                remote: upstream
              Analizado: {}
            """);

        var probar = config.GateFor("  por probar ")!;
        Assert.Equal(("Por Probar", true, "origin", 900000), (probar.State, probar.RequirePushed, probar.Remote, probar.TimeoutMs));
        Assert.Equal(["dotnet build", "dotnet test --no-build"], probar.Run);
        var revision = config.GateFor("En Revision")!;
        Assert.Equal(["dotnet test"], revision.Run);
        Assert.Equal(("upstream", 300000), (revision.Remote, revision.TimeoutMs));
        // A gate with no commands still checks that the work is committed and pushed.
        Assert.Empty(config.GateFor("Analizado")!.Run);
        Assert.Null(config.GateFor("Por Analizar"));
    }

    [Theory]
    [InlineData("gates: [a]", "gates must be a map")]
    [InlineData("gates:\n  Done: dotnet test", "gates.Done must be a map")]
    [InlineData("gates:\n  Done:\n    commands: [x]", "unknown key(s) commands")]
    [InlineData("gates:\n  Done:\n    require_pushed: false", "checks nothing")]
    [InlineData("gates:\n  Done:\n    run: [\"\"]", "must not contain empty commands")]
    [InlineData("gates:\n  Done:\n    run: {a: b}", "must be a command or a list")]
    public void Malformed_gates_stop_the_load(string yaml, string message)
    {
        var ex = Assert.Throws<WorkflowException>(() => Build(yaml));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_when_a_gate_can_outlast_the_stall_timeout()
    {
        var config = Build("claude: {stall_timeout_ms: 900000}\ngates:\n  Por Probar:\n    run: dotnet test");

        Assert.Contains(config.Warnings, w => w.StartsWith("gates.Por Probar.timeout_ms", StringComparison.Ordinal));
    }
}
