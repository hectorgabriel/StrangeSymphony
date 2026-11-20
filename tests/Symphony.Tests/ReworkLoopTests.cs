using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Audit;
using Symphony.Core.Config;
using Symphony.Core.Defects;
using Symphony.Core.Domain;
using Symphony.Core.Orchestration;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

public class DefectConfigTests
{
    private static ServiceConfig Build(string defects) =>
        new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null)
            .Build(WorkflowLoader.Parse($"---\ntracker: {{kind: fake}}\n{defects}\n---\nbody"), "WORKFLOW.md");

    [Fact]
    public void Absent_means_no_defect_reports()
    {
        Assert.Null(Build("").Defects);
    }

    [Fact]
    public void Categories_are_a_list_of_names_or_a_map_to_descriptions()
    {
        var named = Build("defects:\n  states: [Con Defectos]\n  categories:\n    ui: Layout and labels\n    data: Wrong data").Defects!;
        Assert.Equal(["con defectos"], named.States);
        Assert.Equal([KeyValuePair.Create("ui", "Layout and labels"), KeyValuePair.Create("data", "Wrong data")], named.Categories);
        Assert.True(named.IsDefectState("CON DEFECTOS"));
        Assert.False(named.IsDefectState("En Revision"));

        var listed = Build("defects:\n  states: Con Defectos\n  categories: [ui, data]").Defects!;
        Assert.Equal(["ui", "data"], listed.Categories.Select(c => c.Key));
    }

    [Theory]
    [InlineData("defects:\n  categories: [ui]", "defects.states")]
    [InlineData("defects:\n  states: [X]", "defects.categories")]
    [InlineData("defects:\n  states: [X]\n  categories: []", "defects.categories")]
    [InlineData("defects:\n  states: [X]\n  categories: [User Interface]", "defects.categories")]
    [InlineData("defects:\n  states: [X]\n  categories: [ui, ui]", "defects.categories")]
    [InlineData("defects:\n  states: [X]\n  categories: [ui]\n  severity: [high]", "unknown key")]
    [InlineData("defects: [X]", "defects must be a map")]
    public void A_malformed_section_is_rejected(string yaml, string message)
    {
        var ex = Assert.Throws<WorkflowException>(() => Build(yaml));
        Assert.Contains(message, ex.Message);
    }
}

public class DefectTrackerTests
{
    private static readonly DefectSettings Settings = new(["con defectos"], [KeyValuePair.Create("ui", "Layout"), KeyValuePair.Create("data", "")]);

    private readonly List<AgentEvent> _events = [];
    private readonly Issue _issue = TestIssues.Make("1", state: "Por Probar");

    private DefectTracker Tracker() => new(new StateTracker(), Settings, _events.Add);

    private sealed class StateTracker : ITrackerAdapter
    {
        public List<string> Moves { get; } = [];
        public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);
        public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);
        public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
            [new("update_state", "Move the task.", new JsonObject { ["type"] = "object" }, Mutates: true), new("add_comment", "Comment.", new JsonObject { ["type"] = "object" }, Mutates: true)];

        public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
        {
            if (name == "update_state") Moves.Add(arguments["state"]!.GetValue<string>());
            return Task.FromResult(new AgentToolResult(true, new JsonObject { ["state"] = arguments["state"]?.GetValue<string>() }));
        }

        public void Dispose() { }
    }

    private static JsonObject Defects(params (string Category, string Severity)[] defects) =>
        new() { ["defects"] = new JsonArray([.. defects.Select(d => (JsonNode?)new JsonObject { ["category"] = d.Category, ["severity"] = d.Severity })]) };

    private static JsonObject Move(string state) => new() { ["state"] = state };

    [Fact]
    public void Offers_report_defects_with_the_workflows_categories_and_says_moves_back_need_it()
    {
        var specs = Tracker().AgentToolSpecs();

        var report = specs.Single(s => s.Name == DefectTracker.ReportTool);
        var item = report.InputSchema["properties"]!["defects"]!["items"]!;
        Assert.Equal(["ui", "data"], item["properties"]!["category"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(DefectSettings.Severities, item["properties"]!["severity"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("ui: Layout", report.Description);
        Assert.Contains(DefectTracker.ReportTool, specs.Single(s => s.Name == "update_state").Description);
    }

    [Fact]
    public async Task A_move_back_without_a_report_is_refused_and_never_reaches_the_tracker()
    {
        var inner = new StateTracker();
        var tracker = new DefectTracker(inner, Settings, _events.Add);

        var result = await tracker.ExecuteAgentToolAsync("update_state", Move("Con Defectos"), _issue, default);

        Assert.False(result.Success);
        Assert.Equal(DefectTracker.NotReported, result.Output!["error"]!["category"]!.GetValue<string>());
        Assert.Empty(inner.Moves);
        Assert.Empty(_events);

        // Other moves don't need one.
        Assert.True((await tracker.ExecuteAgentToolAsync("update_state", Move("En Revision"), _issue, default)).Success);
        Assert.Equal(["En Revision"], inner.Moves);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_reported_move_back_emits_the_counts_once_and_the_next_one_needs_a_new_report()
    {
        var tracker = Tracker();

        var first = await tracker.ExecuteAgentToolAsync(DefectTracker.ReportTool, Defects(("ui", "minor")), _issue, default);
        Assert.True(first.Success);
        // A second call replaces the first.
        var report = await tracker.ExecuteAgentToolAsync(DefectTracker.ReportTool, Defects(("data", "critical"), ("ui", "major"), ("data", "critical")), _issue, default);
        Assert.Equal(3, report.Output!["recorded"]!.GetValue<int>());

        Assert.True((await tracker.ExecuteAgentToolAsync("update_state", Move("Con Defectos"), _issue, default)).Success);

        var ev = Assert.Single(_events);
        Assert.Equal(AgentEventNames.DefectsReported, ev.Event);
        var d = ev.Defects!;
        Assert.Equal(("Con Defectos", "Por Probar", 3), (d.State, d.FromState, d.Count));
        Assert.Equal(2, d.Counts["data"]["critical"]);
        Assert.Equal(1, d.Counts["ui"]["major"]);
        Assert.False(d.Counts["ui"].ContainsKey("minor"));
        Assert.Equal(new Dictionary<string, int> { ["data"] = 2, ["ui"] = 1 }, d.Categories);
        Assert.Equal(new Dictionary<string, int> { ["critical"] = 2, ["major"] = 1 }, d.Severities);

        var again = await tracker.ExecuteAgentToolAsync("update_state", Move("Con Defectos"), _issue, default);
        Assert.False(again.Success);
    }

    [Theory]
    [InlineData("security", "major", "unknown category")]
    [InlineData("ui", "blocker", "unknown severity")]
    public async Task Unknown_categories_and_severities_are_refused_with_the_valid_ones(string category, string severity, string message)
    {
        var tracker = Tracker();

        var result = await tracker.ExecuteAgentToolAsync(DefectTracker.ReportTool, Defects((category, severity)), _issue, default);

        Assert.False(result.Success);
        Assert.Contains(message, result.Output!["error"]!["message"]!.GetValue<string>());
        Assert.False((await tracker.ExecuteAgentToolAsync("update_state", Move("Con Defectos"), _issue, default)).Success);
    }

    [Fact]
    public async Task The_audit_trail_keeps_the_counts_not_fingerprints()
    {
        var events = new List<AgentEvent>();
        var audited = new AuditedTracker(Tracker(), events.Add);

        await audited.ExecuteAgentToolAsync(DefectTracker.ReportTool, Defects(("ui", "minor"), ("ui", "minor")), _issue, default);

        var call = Assert.Single(events).HostTool!;
        Assert.Equal(2, call.Args["categories"]!["ui"]!.GetValue<int>());
        Assert.Equal(2, call.Args["severities"]!["minor"]!.GetValue<int>());
    }
}

public sealed class ReworkMetricsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"symphony-rework-{Guid.NewGuid():N}.jsonl");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-02T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() => File.Delete(_path);

    private static MetricRecord Run(string state, string model, int startMinute, int endMinute, long tokens, bool rework = false) =>
        new(MetricRecord.Run, "1", "T1", T0.AddMinutes(endMinute), state, model, T0.AddMinutes(startMinute), (endMinute - startMinute) * 60, TotalTokens: tokens,
            Outcome: RunOutcomes.Completed, Rework: rework);

    private static MetricRecord Handoff(string state, int minute) => new(MetricRecord.Handoff, "1", "T1", T0.AddMinutes(minute), state);

    private static MetricRecord SentBack(int minute, string model = "opus") =>
        new(MetricRecord.Defects, "1", "T1", T0.AddMinutes(minute), "Con Defectos", model, FromState: "Por Probar")
        {
            DefectCounts = new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                ["data"] = new Dictionary<string, int> { ["critical"] = 1, ["major"] = 1 },
                ["ui"] = new Dictionary<string, int> { ["minor"] = 1 },
            },
        };

    [Fact]
    public void A_loop_explains_its_rework_and_a_persons_send_back_is_counted_apart()
    {
        var log = new MetricsLog(NullLogger.Instance);
        MetricRecord[] records =
        [
            Run("Plan Aprobado", "sonnet", 0, 30, 1000),
            Handoff("Por Probar", 30),
            Run("Por Probar", "opus", 31, 50, 400),     // the tester, who sends it back at minute 45
            SentBack(45),
            Handoff("Con Defectos", 50),
            // A person triages for an hour; the fix and the second test pass are the loop's rework.
            Run("Plan Aprobado", "sonnet", 105, 125, 600, rework: true),
            Handoff("Por Probar", 125),
            Run("Por Probar", "opus", 126, 140, 300, rework: true),
            Handoff("En Revision", 140),
            // The reviewer sends it back: rework no report explains.
            Run("Plan Aprobado", "sonnet", 200, 210, 250, rework: true),
            Handoff("Por Probar", 210),
            new(MetricRecord.Closed, "1", "T1", T0.AddMinutes(300), "Completado"),
        ];
        foreach (var r in records) log.Add(_path, r);

        // Read back from the file, as after a restart.
        var reloaded = new MetricsLog(NullLogger.Instance);
        reloaded.Load(_path);
        var w = reloaded.Report(T0.AddMinutes(400), TimeZoneInfo.Utc, null).Rework!;

        Assert.Equal((1, 3, 1), (w.Loops, w.Defects, w.IssuesWithLoops));
        Assert.Equal((1, 0), (w.ClosedIssues, w.ClosedWithoutRework));
        Assert.Equal(3600, w.AvgTriageSeconds);
        Assert.Equal((2, 900L, 2040d), (w.LoopReworkRuns, w.LoopReworkTokens, w.LoopReworkSeconds));
        Assert.Equal((1, 250L), (w.OtherReworkRuns, w.OtherReworkTokens));
        Assert.Equal(["critical", "major", "minor"], w.BySeverity.Keys);
        Assert.Equal(new Dictionary<string, int> { ["Plan Aprobado"] = 1 }, w.NextStates);

        var data = w.ByCategory[0];
        Assert.Equal(("data", 2, 1, 1, 900L), (data.Category, data.Defects, data.Loops, data.Issues, data.ReworkTokens));
        Assert.Equal(new Dictionary<string, int> { ["critical"] = 1, ["major"] = 1 }, data.Severities);
        Assert.Equal(("ui", 1), (w.ByCategory[1].Category, w.ByCategory[1].Defects));

        var source = Assert.Single(w.BySource);
        Assert.Equal(("Plan Aprobado", "sonnet", 1, 3), (source.State, source.Model, source.Loops, source.Defects));

        var loop = Assert.Single(w.Recent);
        Assert.Equal(("Por Probar", "Con Defectos", "opus", "sonnet", "Plan Aprobado"), (loop.FromState, loop.State, loop.ReportedBy, loop.WorkModel, loop.NextState));

        var json = StatusServer.MetricsJson(reloaded.Report(T0.AddMinutes(400), TimeZoneInfo.Utc, null));
        Assert.Equal(1, json["issues"]![0]!["loops"]!.GetValue<int>());
        Assert.Equal(2, json["rework"]!["recent"]![0]!["categories"]!["data"]!.GetValue<int>());
        Assert.Equal(1, json["rework"]!["recent"]![0]!["defect_counts"]!["data"]!["critical"]!.GetValue<int>());
    }

    [Fact]
    public void A_loop_nobody_picked_up_yet_is_waiting()
    {
        var log = new MetricsLog(NullLogger.Instance);
        log.Add(_path, Run("Plan Aprobado", "sonnet", 0, 30, 1000));
        log.Add(_path, SentBack(45));

        var loop = Assert.Single(log.Report(T0.AddMinutes(60), TimeZoneInfo.Utc, null).Rework!.Recent);

        Assert.Null(loop.TriageSeconds);
        Assert.Null(loop.NextState);
        Assert.Equal(new Dictionary<string, int> { ["waiting"] = 1 }, log.Report(T0.AddMinutes(60), TimeZoneInfo.Utc, null).Rework!.NextStates);
    }

    [Fact]
    public void No_loops_and_no_rework_means_no_section()
    {
        var log = new MetricsLog(NullLogger.Instance);
        log.Add(_path, Run("Plan Aprobado", "sonnet", 0, 30, 1000));

        Assert.Null(log.Report(T0.AddMinutes(60), TimeZoneInfo.Utc, null).Rework);
    }
}

public class ReworkLoopOrchestratorTests
{
    [Fact]
    public async Task A_defect_report_from_a_session_becomes_a_loop_with_the_runs_model()
    {
        await using var h = new OrchestratorHarness("claude:\n  model: opus");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Emit(new AgentEvent(AgentEventNames.DefectsReported, DateTimeOffset.UtcNow)
        {
            Defects = new DefectReport("Con Defectos", "Todo", new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                ["ui"] = new Dictionary<string, int> { ["major"] = 2 },
            }),
        });
        await Wait.Until(() => h.Orchestrator.GetMetrics().Rework is not null, "loop");

        var loop = Assert.Single(h.Orchestrator.GetMetrics().Rework!.Recent);
        Assert.Equal(("WI-1", "Todo", "Con Defectos", 2, "opus"), (loop.Identifier, loop.FromState, loop.State, loop.Defects, loop.ReportedBy));
    }
}
