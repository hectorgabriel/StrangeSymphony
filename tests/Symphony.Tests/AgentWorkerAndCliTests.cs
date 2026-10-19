using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;

namespace Symphony.Tests;

public sealed class AgentWorkerTests : IDisposable
{
    private sealed class FakeSession(List<string> prompts, Func<int, Task>? onTurn) : IAgentSession
    {
        public bool Disposed { get; private set; }
        public string? ThreadId => "thread";

        public async Task RunTurnAsync(string prompt, Issue currentIssue, CancellationToken ct)
        {
            prompts.Add(prompt);
            if (onTurn is not null) await onTurn(prompts.Count);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory(List<string> prompts, Func<int, Task>? onTurn = null) : IAgentSessionFactory
    {
        public FakeSession? Session { get; private set; }
        public string? Cwd { get; private set; }

        public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
        {
            Cwd = request.WorkspacePath;
            Session = new FakeSession(prompts, onTurn);
            return Task.FromResult<IAgentSession>(Session);
        }
    }

    private readonly TempWorkflow _tmp = new("tracker: {kind: fake}");
    private readonly FakeTracker _tracker = new();

    private EffectiveWorkflow Workflow(string extra = "", string body = "Fix {{ issue.identifier }} attempt={{ attempt }}")
    {
        _tmp.Write($"tracker: {{kind: fake}}\nworkspace: {{root: \"{{ROOT}}\"}}\n{extra}", body);
        var store = _tmp.Store(_tracker);
        return store.LoadInitial();
    }

    private AgentWorker Worker(IAgentSessionFactory factory) =>
        new(new WorkspaceManager(NullLogger<WorkspaceManager>.Instance), factory, NullLogger<AgentWorker>.Instance);

    [Fact]
    public async Task First_turn_uses_rendered_prompt_then_continuation_guidance_until_inactive()
    {
        var wf = Workflow();
        var issue = TestIssues.Make("1");
        _tracker.Put(issue);
        var prompts = new List<string>();
        var factory = new FakeFactory(prompts, turn =>
        {
            if (turn == 3) _tracker.Put(TestIssues.Make("1", state: "Done"));
            return Task.CompletedTask;
        });
        var events = new List<AgentEvent>();

        await Worker(factory).RunAsync(issue, 2, wf, events.Add, _ => { }, default);

        Assert.Equal(3, prompts.Count);
        Assert.Equal("Fix WI-1 attempt=2", prompts[0]);
        Assert.StartsWith("Continue working on WI-1", prompts[1]);
        Assert.DoesNotContain("Fix WI-1", prompts[1]);
        Assert.True(factory.Session!.Disposed);
        Assert.Equal(Path.Combine(_tmp.WorkspaceRoot, "WI-1"), factory.Cwd);
        Assert.Equal(3, events.Count(e => e.Event == AgentEventNames.TurnStarted));
    }

    [Fact]
    public async Task Stops_at_max_turns()
    {
        var wf = Workflow("agent: {max_turns: 2}");
        var issue = TestIssues.Make("1");
        _tracker.Put(issue);
        var prompts = new List<string>();
        await Worker(new FakeFactory(prompts)).RunAsync(issue, null, wf, _ => { }, _ => { }, default);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public async Task Stops_when_the_new_state_is_routed_to_another_model()
    {
        var wf = Workflow("claude:\n  model: opus\n  model_by_state: {In Progress: qwen}");
        var issue = TestIssues.Make("1");
        _tracker.Put(issue);
        var prompts = new List<string>();
        var factory = new FakeFactory(prompts, turn =>
        {
            if (turn == 1) _tracker.Put(TestIssues.Make("1", state: "In Progress"));
            return Task.CompletedTask;
        });

        await Worker(factory).RunAsync(issue, null, wf, _ => { }, _ => { }, default);

        Assert.Single(prompts);
        Assert.True(factory.Session!.Disposed);
    }

    [Fact]
    public async Task Before_run_failure_aborts_but_after_run_still_runs()
    {
        var wf = Workflow("hooks:\n  before_run: exit 1\n  after_run: touch after_run.txt");
        var issue = TestIssues.Make("1");
        var prompts = new List<string>();
        await Assert.ThrowsAsync<WorkerFailedException>(() => Worker(new FakeFactory(prompts)).RunAsync(issue, null, wf, _ => { }, _ => { }, default));
        Assert.Empty(prompts);
        Assert.True(File.Exists(Path.Combine(_tmp.WorkspaceRoot, "WI-1", "after_run.txt")));
    }

    [Fact]
    public async Task Turn_failure_fails_worker_and_runs_after_run()
    {
        var wf = Workflow("hooks:\n  after_run: touch after_run.txt");
        var issue = TestIssues.Make("1");
        var factory = new FakeFactory([], _ => throw new AgentException(AgentErrorCodes.TurnFailed, "nope"));
        var ex = await Assert.ThrowsAsync<WorkerFailedException>(() => Worker(factory).RunAsync(issue, null, wf, _ => { }, _ => { }, default));
        Assert.Contains("turn_failed", ex.Message);
        Assert.True(factory.Session!.Disposed);
        Assert.True(File.Exists(Path.Combine(_tmp.WorkspaceRoot, "WI-1", "after_run.txt")));
    }

    [Fact]
    public async Task Prompt_render_error_fails_attempt()
    {
        var wf = Workflow(body: "{{ issue.unknown_field }}");
        var ex = await Assert.ThrowsAsync<WorkerFailedException>(() =>
            Worker(new FakeFactory([])).RunAsync(TestIssues.Make("1"), null, wf, _ => { }, _ => { }, default));
        Assert.Contains("prompt error", ex.Message);
    }

    public void Dispose() => _tmp.Dispose();
}

public class CliTests
{
    [Fact]
    public async Task Nonexistent_explicit_workflow_path_fails()
    {
        Assert.Equal(1, await SymphonyCli.RunAsync(["/nope/WORKFLOW.md"]));
    }

    [Fact]
    public async Task Invalid_arguments_fail()
    {
        Assert.Equal(2, await SymphonyCli.RunAsync(["--bogus"]));
    }

    [Fact]
    public async Task Invalid_workflow_config_fails_startup()
    {
        using var tmp = new TempWorkflow("tracker: {kind: jira}");
        Assert.Equal(1, await SymphonyCli.RunAsync([tmp.Path]));
    }

    [Fact]
    public async Task Help_exits_zero()
    {
        Assert.Equal(0, await SymphonyCli.RunAsync(["--help"]));
    }
}

public class ExampleWorkflowTests
{
    [Fact]
    public void Shipped_example_workflow_loads_and_renders_strictly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WORKFLOW.example.md");
        var definition = WorkflowLoader.Load(path);
        var builder = new Symphony.Core.Config.ConfigBuilder(
            [new Symphony.AzureDevOps.AzureDevOpsAdapterFactory()],
            n => n == "AZURE_DEVOPS_PAT" ? "pat" : null);
        var config = builder.Build(definition, path);

        Assert.Equal("azure_devops", config.Tracker.Kind);
        Assert.Equal(["agent"], config.Tracker.RequiredLabels);
        Assert.Equal("acceptEdits", config.Claude.PermissionMode);

        var withLabels = Symphony.Core.Templating.PromptRenderer.Render(definition.PromptTemplate, TestIssues.Make("1", labels: ["agent", "api"]), 2);
        Assert.Contains("Tags: agent, api", withLabels);
        Assert.Contains("attempt 2", withLabels);

        var plain = Symphony.Core.Templating.PromptRenderer.Render(definition.PromptTemplate, TestIssues.Make("1"), null);
        Assert.DoesNotContain("Tags:", plain);
        Assert.DoesNotContain("attempt", plain);
    }

    [Theory]
    [InlineData("Por Analizar", "analyst.md", "/bossdream-mode plan", "\"Analizado\"")]
    [InlineData("Por Desarrollar", "developer.md", "/bossdream-mode plan", "\"Plan Propuesto\"")]
    [InlineData("Plan Aprobado", "developer.md", "/bossdream-mode implement", "\"Por Probar\"")]
    [InlineData("Por Probar", "tester.md", "/bossdream-mode implement", "\"Con Defectos\"")]
    [InlineData("Por Documentar", "documentador.md", "/bossdream-mode document", "\"Documentado\"")]
    public void Sdlc_example_workflow_routes_each_active_status_to_its_role(string state, string role, string mode, string handoff)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WORKFLOW.sdlc.example.md");
        var definition = WorkflowLoader.Load(path);
        var config = new Symphony.Core.Config.ConfigBuilder(
            [new Symphony.Wrike.WrikeAdapterFactory()],
            n => n == "WRIKE_TOKEN" ? "token" : null).Build(definition, path);

        Assert.Contains(state, config.Tracker.ActiveStates);

        var prompt = Symphony.Core.Templating.PromptRenderer.Render(definition.PromptTemplate, TestIssues.Make("1", state: state), null);
        Assert.Contains($".claude/agents/{role}", prompt);
        Assert.Contains(mode, prompt);
        Assert.Contains(handoff, prompt);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, "/bossdream-mode "));
        Assert.DoesNotContain("Stage: unknown", prompt);
    }

    [Fact]
    public void Sdlc_example_workflow_flags_a_status_without_a_stage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WORKFLOW.sdlc.example.md");
        var definition = WorkflowLoader.Load(path);

        var prompt = Symphony.Core.Templating.PromptRenderer.Render(definition.PromptTemplate, TestIssues.Make("1", state: "En Proceso"), null);

        Assert.Contains("Stage: unknown", prompt);
        Assert.DoesNotContain("/bossdream-mode ", prompt);
    }
}
