using System.Text.Json.Nodes;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Core.Defects;

/// <summary>
/// The tracker as an agent session sees it when the workflow has <c>defects</c> (docs/rework-loops.md). It adds
/// <c>report_defects</c>, where the agent lists the defects it found by category and severity only, and holds
/// <c>update_state</c> to a send-back status until this session has reported. When that move succeeds, the report
/// goes to the orchestrator as <see cref="AgentEventNames.DefectsReported"/>: one rework loop, with its reasons.
/// Does not own <paramref name="inner"/>.
/// </summary>
public sealed class DefectTracker(ITrackerAdapter inner, DefectSettings settings, Action<AgentEvent>? onEvent = null) : ITrackerAdapter
{
    public const string ReportTool = "report_defects";
    public const string NotReported = "defects_not_reported";
    private const int MaxDefects = 200;

    private readonly object _gate = new();
    private DefectReport? _pending;

    public ITrackerAdapter Inner => inner;

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<string> SecretEnvironmentNames() => inner.SecretEnvironmentNames();

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs()
    {
        var specs = inner.AgentToolSpecs();
        if (specs.Any(s => s.Name == ReportTool))
        {
            throw new InvalidOperationException($"the tracker already has a tool named '{ReportTool}'");
        }

        return [.. specs.Select(s => s.Name == Gates.GatedTracker.UpdateStateTool ? Held(s) : s), Spec()];
    }

    private string Targets() => string.Join(" or ", settings.States.Select(s => $"\"{s}\""));

    private AgentToolSpec Held(AgentToolSpec spec) => spec with
    {
        Description = spec.Description + $" Moving to {Targets()} needs {ReportTool} first, in this session.",
    };

    private AgentToolSpec Spec()
    {
        var categories = string.Join("; ", settings.Categories.Select(c => c.Value.Length > 0 ? $"{c.Key}: {c.Value}" : c.Key));
        return new AgentToolSpec(ReportTool,
            $"Before moving the task to {Targets()}, list every defect you found, each with one category and a severity. "
            + "Only the categories and severities are recorded, to measure why work is sent back; describe each defect with add_comment as usual. "
            + $"Calling it again replaces the earlier list. Categories: {categories}.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["defects"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = MaxDefects,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["category"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. settings.Categories.Select(c => (JsonNode?)c.Key)]) },
                                ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. DefectSettings.Severities.Select(s => (JsonNode?)s)]) },
                            },
                            ["required"] = new JsonArray("category", "severity"),
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["required"] = new JsonArray("defects"),
                ["additionalProperties"] = false,
            },
            Mutates: false);
    }

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        if (name == ReportTool)
        {
            return Report(arguments);
        }

        if (name != Gates.GatedTracker.UpdateStateTool
            || arguments["state"] is not JsonValue v || !v.TryGetValue(out string? target)
            || !settings.IsDefectState(target))
        {
            return await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        }

        DefectReport? report;
        lock (_gate)
        {
            report = _pending;
        }

        if (report is null)
        {
            return AgentToolResult.Fail(NotReported,
                $"The status was not changed: call {ReportTool} with the defects you found before moving the task to \"{target}\".");
        }

        var result = await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        if (result.Success)
        {
            lock (_gate)
            {
                _pending = null;
            }

            var state = result.Output?["state"] is JsonValue s && s.TryGetValue(out string? moved) ? moved : target;
            onEvent?.Invoke(new AgentEvent(AgentEventNames.DefectsReported, DateTimeOffset.UtcNow)
            {
                Message = $"sent back to \"{state}\" with {report.Count} defect(s)",
                Defects = report with { State = state, FromState = issue.State },
            });
        }

        return result;
    }

    private AgentToolResult Report(JsonObject arguments)
    {
        if (arguments["defects"] is not JsonArray list || list.Count == 0)
        {
            return AgentToolResult.Fail("invalid_arguments", "defects must be a non-empty list of { category, severity }");
        }

        if (list.Count > MaxDefects)
        {
            return AgentToolResult.Fail("invalid_arguments", $"at most {MaxDefects} defects per report");
        }

        var counts = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            var category = Str(item?["category"])?.Trim().ToLowerInvariant();
            var severity = Str(item?["severity"])?.Trim().ToLowerInvariant();
            if (category is null || !settings.Categories.Any(c => c.Key == category))
            {
                return AgentToolResult.Fail("invalid_arguments",
                    $"unknown category '{Str(item?["category"])}'; use one of: {string.Join(", ", settings.Categories.Select(c => c.Key))}");
            }

            if (severity is null || !DefectSettings.Severities.Contains(severity))
            {
                return AgentToolResult.Fail("invalid_arguments",
                    $"unknown severity '{Str(item?["severity"])}'; use one of: {string.Join(", ", DefectSettings.Severities)}");
            }

            if (!counts.TryGetValue(category, out var bySeverity))
            {
                counts[category] = bySeverity = new SortedDictionary<string, int>(StringComparer.Ordinal);
            }

            bySeverity[severity] = bySeverity.GetValueOrDefault(severity) + 1;
        }

        var report = new DefectReport(null, null, counts.ToDictionary(c => c.Key, c => (IReadOnlyDictionary<string, int>)c.Value));
        lock (_gate)
        {
            _pending = report;
        }

        return new AgentToolResult(true, new JsonObject
        {
            ["recorded"] = report.Count,
            ["categories"] = Counts(report.Categories),
            ["severities"] = Counts(report.Severities),
            ["next"] = $"Describe each defect with add_comment, then move the task to {Targets()}.",
        });
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static JsonObject Counts(IReadOnlyDictionary<string, int> counts)
    {
        var o = new JsonObject();
        foreach (var (k, n) in counts)
        {
            o[k] = n;
        }

        return o;
    }

    public void Dispose()
    {
        // The tracker belongs to the workflow and outlives the session.
    }
}

/// <summary>
/// The defects an agent reported when it sent an issue back (<see cref="FromState"/> to <see cref="State"/>):
/// <see cref="Counts"/> is category -> severity -> number of defects. No defect text.
/// </summary>
public sealed record DefectReport(
    string? State,
    string? FromState,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counts)
{
    public int Count => Counts.Values.Sum(s => s.Values.Sum());

    public IReadOnlyDictionary<string, int> Categories => CategoriesOf(Counts);

    public IReadOnlyDictionary<string, int> Severities => SeveritiesOf(Counts);

    public static IReadOnlyDictionary<string, int> CategoriesOf(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts) =>
        counts.OrderByDescending(c => c.Value.Values.Sum()).ThenBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value.Values.Sum());

    public static IReadOnlyDictionary<string, int> SeveritiesOf(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts) =>
        counts.Values.SelectMany(s => s).GroupBy(kv => kv.Key)
            .OrderBy(g => Array.IndexOf([.. Config.DefectSettings.Severities], g.Key)).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
}
