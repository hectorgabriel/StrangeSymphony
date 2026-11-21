using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Symphony.Core.Domain;

namespace Symphony.Core.Orchestration;

/// <summary>
/// One line of the metrics log. <see cref="Kind"/> is <c>run</c> for a finished agent run, <c>handoff</c> when
/// Symphony let go of an issue that is still open, <c>closed</c> when an issue reached a terminal state, and
/// <c>gate</c> for a verification gate run: <see cref="State"/> is the gate's status, <see cref="FromState"/>
/// the status the issue was in, and <see cref="Outcome"/> <c>passed</c> or the failed check's category.
/// <c>defects</c> is a rework loop (docs/rework-loops.md): an agent sent the issue back from <see cref="FromState"/>
/// to <see cref="State"/>; <see cref="DefectCounts"/> is the number of defects it reported per category and severity.
/// </summary>
public sealed record MetricRecord(
    string Kind,
    string IssueId,
    string Identifier,
    DateTimeOffset At,
    string? State = null,
    string? Model = null,
    DateTimeOffset? StartedAt = null,
    double Seconds = 0,
    int Turns = 0,
    long InputTokens = 0,
    long OutputTokens = 0,
    long TotalTokens = 0,
    int? Attempt = null,
    string? Outcome = null,
    string? Error = null,
    bool Resumed = false,
    bool Rework = false,
    string? FromState = null,
    // The agent runner (agent.runner) of a run or defects record; missing on lines written before runners existed.
    string? Runner = null)
{
    public const string Run = "run";
    public const string Handoff = "handoff";
    public const string Closed = "closed";
    public const string Gate = "gate";
    public const string GatePassed = "passed";
    public const string Defects = "defects";

    // Category -> severity -> number of defects.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? DefectCounts { get; init; }
}

public static class RunOutcomes
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stalled = "stalled";
    public const string Released = "released";
    public const string Budget = "budget";
    public const string Interrupted = "interrupted";

    public static bool IsFailure(string? outcome) => outcome is Failed or Stalled;
}

/// <summary>Today's token use against the configured caps; a cap of 0 means no limit.</summary>
public sealed record BudgetStatus(string Day, long DayTokens, long MaxTokensPerDay, long MaxTokensPerIssue)
{
    public bool DayExceeded => MaxTokensPerDay > 0 && DayTokens >= MaxTokensPerDay;
}

public sealed record MetricsTotals(
    int Runs,
    int Issues,
    int ClosedIssues,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double AgentSeconds,
    int FailedRuns,
    int ReworkRuns,
    int ResumedRuns,
    double? AvgCycleSeconds,
    int GateChecks = 0,
    int GateRejections = 0);

public sealed record StateMetrics(
    string State,
    int Runs,
    int Issues,
    double AgentSeconds,
    double AvgSecondsPerIssue,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    int FailedRuns,
    int ReworkRuns,
    IReadOnlyDictionary<string, long> TokensByModel);

/// <summary>
/// Verification gate results for one target status. A <em>hand-off</em> is one attempt to get an issue past the
/// gate: the checks up to and including the one that passed (or every check so far, if none has passed yet).
/// <see cref="FirstTryHandoffs"/> are the hand-offs whose first check passed.
/// </summary>
public sealed record GateMetrics(
    string State,
    int Checks,
    int Passed,
    int Rejected,
    int Issues,
    int Handoffs,
    int FirstTryHandoffs,
    double AvgSeconds,
    IReadOnlyDictionary<string, int> RejectionsByCategory);

/// <summary>Runs of one model on one runner; <see cref="Runner"/> is <c>claude</c> for lines written before runners existed.</summary>
public sealed record ModelMetrics(string Model, int Runs, long InputTokens, long OutputTokens, long TotalTokens, double AgentSeconds, string Runner = Agents.AgentRunners.Claude);

public sealed record DayMetrics(string Day, int Runs, int ClosedIssues, long TotalTokens, double AgentSeconds);

public sealed record IssueStateMetrics(string State, int Runs, double AgentSeconds, long TotalTokens);

public sealed record IssueMetrics(
    string IssueId,
    string Identifier,
    int Runs,
    double AgentSeconds,
    long TotalTokens,
    int FailedRuns,
    int ReworkRuns,
    DateTimeOffset FirstStartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? ClosedAt,
    double? CycleSeconds,
    IReadOnlyList<IssueStateMetrics> States,
    int GateRejections = 0,
    int Loops = 0);

public sealed record MetricsReport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? Since,
    MetricsTotals Totals,
    IReadOnlyList<StateMetrics> ByState,
    IReadOnlyList<ModelMetrics> ByModel,
    IReadOnlyList<DayMetrics> ByDay,
    IReadOnlyList<IssueMetrics> Issues,
    BudgetStatus? Budget,
    IReadOnlyList<GateMetrics>? ByGate = null,
    ReworkMetrics? Rework = null);

/// <summary>
/// One rework loop: an agent sent the issue back from <see cref="FromState"/> to <see cref="State"/> with
/// <see cref="Defects"/> defects. <see cref="WorkModel"/> and <see cref="WorkState"/> are the run whose work was sent
/// back (the latest one before, in another status). <see cref="TriageSeconds"/> runs from the send-back to the next
/// agent run, which started in <see cref="NextState"/>; null while nobody has picked it up. The rework fields add up the
/// rework runs from then until the issue gets past <see cref="FromState"/> again.
/// </summary>
public sealed record LoopMetrics(
    string IssueId,
    string Identifier,
    DateTimeOffset At,
    string? FromState,
    string? State,
    int Defects,
    // Category -> severity -> number of defects.
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> DefectCounts,
    string? ReportedBy,
    string? WorkModel,
    string? WorkState,
    double? TriageSeconds,
    string? NextState,
    int ReworkRuns,
    long ReworkTokens,
    double ReworkSeconds)
{
    public IReadOnlyDictionary<string, int> Categories => Symphony.Core.Defects.DefectReport.CategoriesOf(DefectCounts);

    public IReadOnlyDictionary<string, int> Severities => Symphony.Core.Defects.DefectReport.SeveritiesOf(DefectCounts);
}

/// <summary>
/// Defects of one category across all loops. <see cref="ReworkTokens"/> is the rework of the loops this category
/// was part of; a loop with several categories counts for each, so the column doesn't add up to the total.
/// </summary>
public sealed record DefectCategoryMetrics(string Category, int Defects, int Loops, int Issues, IReadOnlyDictionary<string, int> Severities, long ReworkTokens);

/// <summary>Loops by the run whose work was sent back: its status and model.</summary>
public sealed record LoopSourceMetrics(string State, string Model, int Loops, int Defects);

/// <summary>
/// Why work goes back (docs/rework-loops.md). <see cref="OtherReworkRuns"/> are rework runs that no defect report
/// explains: a person sent the issue back, for example from review.
/// </summary>
public sealed record ReworkMetrics(
    int Loops,
    int Defects,
    int IssuesWithLoops,
    int ClosedIssues,
    int ClosedWithoutRework,
    double? AvgTriageSeconds,
    int LoopReworkRuns,
    long LoopReworkTokens,
    double LoopReworkSeconds,
    int OtherReworkRuns,
    long OtherReworkTokens,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyList<DefectCategoryMetrics> ByCategory,
    IReadOnlyList<LoopSourceMetrics> BySource,
    IReadOnlyDictionary<string, int> NextStates,
    IReadOnlyList<LoopMetrics> Recent);

/// <summary>
/// The history behind <c>/api/v1/metrics</c>: an append-only JSON-lines file next to the workspaces, one line
/// per finished run, handoff or closed issue. It is read once at startup; reports are computed on demand.
/// Safe to call from the scheduler loop and the HTTP surface at the same time.
/// </summary>
public sealed class MetricsLog(ILogger logger)
{
    public const string FileName = ".symphony-metrics.jsonl";
    private const string DefaultModel = "default";
    private const int ReportDays = 14;
    private const int ReportIssues = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly object _gate = new();
    private readonly List<MetricRecord> _records = [];
    private readonly Dictionary<string, long> _issueTokens = new();
    // Issue id -> normalized states whose stage was already handed off once; a later run there is rework.
    private readonly Dictionary<string, HashSet<string>> _handedOff = new();
    private readonly Dictionary<string, string> _lastRunState = new();
    private string? _lastError;

    public static string PathFor(string workspaceRoot) => Path.Combine(Path.GetFullPath(workspaceRoot), FileName);

    /// <summary>Loads the history; unreadable lines are skipped, so one bad line never loses the rest.</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var skipped = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                MetricRecord? record = null;
                try
                {
                    record = JsonSerializer.Deserialize<MetricRecord>(line, Json);
                }
                catch (JsonException)
                {
                }

                if (record is null || string.IsNullOrEmpty(record.IssueId) || string.IsNullOrEmpty(record.Kind))
                {
                    skipped++;
                    continue;
                }

                lock (_gate)
                {
                    Apply(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("action=metrics_load outcome=failed path={Path} reason={Reason}", path, ex.Message);
            return;
        }

        logger.LogInformation("action=metrics_load outcome=completed path={Path} records={Records} skipped={Skipped}", path, _records.Count, skipped);
    }

    /// <summary>Tokens used by every finished run of the issue, across all its stages.</summary>
    public long IssueTokens(string issueId)
    {
        lock (_gate)
        {
            return _issueTokens.GetValueOrDefault(issueId);
        }
    }

    /// <summary>True when the issue's stage in <paramref name="state"/> was handed off before, so a run there repeats it.</summary>
    public bool IsRework(string issueId, string state)
    {
        lock (_gate)
        {
            return _handedOff.TryGetValue(issueId, out var states) && states.Contains(StateNames.Normalize(state));
        }
    }

    public void Add(string path, MetricRecord record)
    {
        lock (_gate)
        {
            Apply(record);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n");
                _lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex.Message != _lastError)
                {
                    _lastError = ex.Message;
                    logger.LogWarning("action=metrics_save outcome=failed path={Path} reason={Reason} note=continuing_in_memory", path, ex.Message);
                }
            }
        }
    }

    private void Apply(MetricRecord r)
    {
        _records.Add(r);
        switch (r.Kind)
        {
            case MetricRecord.Run:
                _issueTokens[r.IssueId] = _issueTokens.GetValueOrDefault(r.IssueId) + r.TotalTokens;
                if (r.State is not null)
                {
                    _lastRunState[r.IssueId] = StateNames.Normalize(r.State);
                }

                break;

            case MetricRecord.Handoff:
                // Only a move to another state hands the stage off; a hold in the same state does not.
                if (r.State is not null && _lastRunState.TryGetValue(r.IssueId, out var state) && StateNames.Normalize(r.State) != state)
                {
                    if (!_handedOff.TryGetValue(r.IssueId, out var states))
                    {
                        _handedOff[r.IssueId] = states = [];
                    }

                    states.Add(state);
                }

                break;
        }
    }

    public MetricsReport Report(DateTimeOffset now, TimeZoneInfo zone, BudgetStatus? budget)
    {
        List<MetricRecord> records;
        lock (_gate)
        {
            records = [.. _records];
        }

        var runs = records.Where(r => r.Kind == MetricRecord.Run).ToList();
        var gateChecks = records.Where(r => r.Kind == MetricRecord.Gate).ToList();
        var rejectionsByIssue = gateChecks.Where(r => r.Outcome != MetricRecord.GatePassed)
            .GroupBy(r => r.IssueId).ToDictionary(g => g.Key, g => g.Count());
        var closedAt = records.Where(r => r.Kind == MetricRecord.Closed)
            .GroupBy(r => r.IssueId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.At));

        var (loops, otherRework) = Loops(records);
        var loopsByIssue = loops.GroupBy(l => l.IssueId).ToDictionary(g => g.Key, g => g.Count());
        var issues = runs.GroupBy(r => r.IssueId).Select(g =>
        {
            var first = g.Min(r => r.StartedAt ?? r.At);
            // A reopened issue is open again: only a close after its last run counts.
            DateTimeOffset? closed = closedAt.TryGetValue(g.Key, out var c) && c >= g.Max(r => r.At) ? c : null;
            return new IssueMetrics(
                g.Key,
                g.Last().Identifier,
                g.Count(),
                Round(g.Sum(r => r.Seconds)),
                g.Sum(r => r.TotalTokens),
                g.Count(r => RunOutcomes.IsFailure(r.Outcome)),
                g.Count(r => r.Rework),
                first,
                closed ?? g.Max(r => r.At),
                closed,
                closed is { } at ? Round((at - first).TotalSeconds) : null,
                g.GroupBy(r => r.State ?? "").Select(s => new IssueStateMetrics(s.Key, s.Count(), Round(s.Sum(r => r.Seconds)), s.Sum(r => r.TotalTokens))).ToList(),
                rejectionsByIssue.GetValueOrDefault(g.Key),
                loopsByIssue.GetValueOrDefault(g.Key));
        }).ToList();

        var cycles = issues.Where(i => i.CycleSeconds is not null).Select(i => i.CycleSeconds!.Value).ToList();
        var totals = new MetricsTotals(
            runs.Count,
            issues.Count,
            issues.Count(i => i.ClosedAt is not null),
            runs.Sum(r => r.InputTokens),
            runs.Sum(r => r.OutputTokens),
            runs.Sum(r => r.TotalTokens),
            Round(runs.Sum(r => r.Seconds)),
            runs.Count(r => RunOutcomes.IsFailure(r.Outcome)),
            runs.Count(r => r.Rework),
            runs.Count(r => r.Resumed),
            cycles.Count > 0 ? Round(cycles.Average()) : null,
            gateChecks.Count,
            gateChecks.Count(r => r.Outcome != MetricRecord.GatePassed));

        var byState = runs.GroupBy(r => r.State ?? "").Select(g =>
        {
            var issueCount = g.Select(r => r.IssueId).Distinct().Count();
            var seconds = g.Sum(r => r.Seconds);
            return new StateMetrics(
                g.Key,
                g.Count(),
                issueCount,
                Round(seconds),
                Round(seconds / Math.Max(1, issueCount)),
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens),
                g.Sum(r => r.TotalTokens),
                g.Count(r => RunOutcomes.IsFailure(r.Outcome)),
                g.Count(r => r.Rework),
                g.GroupBy(r => r.Model ?? DefaultModel).ToDictionary(m => m.Key, m => m.Sum(r => r.TotalTokens)));
        }).OrderByDescending(s => s.TotalTokens).ToList();

        var byModel = runs.GroupBy(r => (Runner: Agents.AgentRunners.Normalize(r.Runner), Model: r.Model ?? DefaultModel))
            .Select(g => new ModelMetrics(g.Key.Model, g.Count(), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.TotalTokens), Round(g.Sum(r => r.Seconds)), g.Key.Runner))
            .OrderByDescending(m => m.TotalTokens).ToList();

        var today = Day(now, zone);
        var closedDays = issues.Where(i => i.ClosedAt is not null).GroupBy(i => Day(i.ClosedAt!.Value, zone)).ToDictionary(g => g.Key, g => g.Count());
        var runDays = runs.GroupBy(r => Day(r.At, zone)).ToDictionary(g => g.Key, g => g.ToList());
        var byDay = Enumerable.Range(0, ReportDays).Select(i => today.AddDays(-i)).Select(day =>
        {
            var dayRuns = runDays.GetValueOrDefault(day) ?? [];
            return new DayMetrics(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dayRuns.Count,
                closedDays.GetValueOrDefault(day),
                dayRuns.Sum(r => r.TotalTokens),
                Round(dayRuns.Sum(r => r.Seconds)));
        }).ToList();

        return new MetricsReport(
            now,
            runs.Count > 0 ? runs.Min(r => r.StartedAt ?? r.At) : null,
            totals,
            byState,
            byModel,
            byDay,
            issues.OrderByDescending(i => i.LastActivityAt).Take(ReportIssues).ToList(),
            budget,
            ByGate(gateChecks),
            Rework(loops, otherRework, issues));
    }

    private const int ReportLoops = 30;
    private const string Waiting = "waiting";

    private sealed class OpenLoop(MetricRecord report, MetricRecord? work)
    {
        public MetricRecord Report { get; } = report;
        public MetricRecord? Work { get; } = work;
        public MetricRecord? Next { get; set; }
        public bool Retested { get; set; }
        public int Runs { get; set; }
        public long Tokens { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>
    /// Pairs each <c>defects</c> record with what followed: the next run (triage), and the rework runs until the
    /// issue gets past the stage that sent it back. Rework runs outside any loop are returned separately.
    /// </summary>
    private static (List<LoopMetrics> Loops, List<MetricRecord> Other) Loops(List<MetricRecord> records)
    {
        var loops = new List<LoopMetrics>();
        var other = new List<MetricRecord>();
        foreach (var issue in records.GroupBy(r => r.IssueId))
        {
            OpenLoop? open = null;
            MetricRecord? lastRun = null;
            var history = new List<MetricRecord>();
            void Close()
            {
                if (open is null) return;
                var r = open.Report;
                var next = open.Next;
                loops.Add(new LoopMetrics(
                    r.IssueId, r.Identifier, r.At, r.FromState, r.State,
                    r.DefectCounts?.Values.Sum(c => c.Values.Sum()) ?? 0,
                    r.DefectCounts ?? new Dictionary<string, IReadOnlyDictionary<string, int>>(),
                    r.Model,
                    open.Work is null ? null : open.Work.Model ?? DefaultModel,
                    open.Work?.State,
                    next is null ? null : Round(((next.StartedAt ?? next.At) - r.At).TotalSeconds),
                    next?.State,
                    open.Runs,
                    open.Tokens,
                    Round(open.Seconds)));
                open = null;
            }

            foreach (var rec in issue)
            {
                switch (rec.Kind)
                {
                    case MetricRecord.Defects:
                        Close();
                        // The work sent back: the latest run before the report, in a status other than the reporter's.
                        var from = StateNames.Normalize(rec.FromState);
                        var work = history.LastOrDefault(h => StateNames.Normalize(h.State) != from && (h.StartedAt ?? h.At) <= rec.At);
                        open = new OpenLoop(rec, work);
                        break;

                    case MetricRecord.Run:
                        history.Add(rec);
                        lastRun = rec;
                        // The run that made the report ended after it; only runs started since belong to the loop.
                        if (open is not null && (rec.StartedAt ?? rec.At) >= open.Report.At)
                        {
                            open.Next ??= rec;
                            if (StateNames.Normalize(rec.State) == StateNames.Normalize(open.Report.FromState))
                            {
                                open.Retested = true;
                            }

                            if (rec.Rework)
                            {
                                open.Runs++;
                                open.Tokens += rec.TotalTokens;
                                open.Seconds += rec.Seconds;
                            }
                        }
                        else if (rec.Rework)
                        {
                            other.Add(rec);
                        }

                        break;

                    case MetricRecord.Handoff when open is { Retested: true }
                        && StateNames.Normalize(rec.State) != StateNames.Normalize(open.Report.State):
                    case MetricRecord.Closed:
                        // Past the stage that sent it back, or done: later rework is someone else's send-back.
                        Close();
                        break;
                }
            }

            Close();
        }

        return (loops, other);
    }

    private static ReworkMetrics? Rework(List<LoopMetrics> loops, List<MetricRecord> other, List<IssueMetrics> issues)
    {
        if (loops.Count == 0 && other.Count == 0)
        {
            return null;
        }

        static IReadOnlyDictionary<string, int> Sum(IEnumerable<IReadOnlyDictionary<string, int>> counts) =>
            Symphony.Core.Defects.DefectReport.SeveritiesOf(new Dictionary<string, IReadOnlyDictionary<string, int>> { [""] = counts.SelectMany(c => c)
                .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)) });

        var triage = loops.Where(l => l.TriageSeconds is not null).Select(l => l.TriageSeconds!.Value).ToList();
        var closed = issues.Where(i => i.ClosedAt is not null).ToList();
        var byCategory = loops
            .SelectMany(l => l.DefectCounts.Select(c => (Loop: l, Category: c.Key, Severities: c.Value)))
            .GroupBy(x => x.Category)
            .Select(g => new DefectCategoryMetrics(
                g.Key,
                g.Sum(x => x.Severities.Values.Sum()),
                g.Count(),
                g.Select(x => x.Loop.IssueId).Distinct().Count(),
                Sum(g.Select(x => x.Severities)),
                g.Sum(x => x.Loop.ReworkTokens)))
            .OrderByDescending(c => c.Defects).ThenBy(c => c.Category, StringComparer.Ordinal).ToList();
        var bySource = loops
            .GroupBy(l => (State: l.WorkState ?? "", Model: l.WorkModel ?? DefaultModel))
            .Select(g => new LoopSourceMetrics(g.Key.State, g.Key.Model, g.Count(), g.Sum(l => l.Defects)))
            .OrderByDescending(s => s.Loops).ThenBy(s => s.State, StringComparer.Ordinal).ToList();
        var next = loops.GroupBy(l => l.NextState ?? Waiting).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());

        return new ReworkMetrics(
            loops.Count,
            loops.Sum(l => l.Defects),
            loops.Select(l => l.IssueId).Distinct().Count(),
            closed.Count,
            closed.Count(i => i.ReworkRuns == 0 && i.Loops == 0),
            triage.Count > 0 ? Round(triage.Average()) : null,
            loops.Sum(l => l.ReworkRuns),
            loops.Sum(l => l.ReworkTokens),
            Round(loops.Sum(l => l.ReworkSeconds)),
            other.Count,
            other.Sum(r => r.TotalTokens),
            Sum(loops.Select(l => l.Severities)),
            byCategory,
            bySource,
            next,
            loops.OrderByDescending(l => l.At).Take(ReportLoops).ToList());
    }

    private static List<GateMetrics> ByGate(List<MetricRecord> checks) =>
        checks.GroupBy(r => StateNames.Normalize(r.State)).Select(g =>
        {
            var rejected = g.Where(r => r.Outcome != MetricRecord.GatePassed).ToList();
            int handoffs = 0, firstTry = 0;
            foreach (var issue in g.GroupBy(r => r.IssueId))
            {
                var first = true;
                foreach (var check in issue.OrderBy(r => r.At))
                {
                    if (first) handoffs++;
                    var passed = check.Outcome == MetricRecord.GatePassed;
                    if (first && passed) firstTry++;
                    // A pass ends the hand-off; the issue's next check here starts a new one (it came back).
                    first = passed;
                }
            }

            return new GateMetrics(
                g.Last().State ?? g.Key,
                g.Count(),
                g.Count() - rejected.Count,
                rejected.Count,
                g.Select(r => r.IssueId).Distinct().Count(),
                handoffs,
                firstTry,
                Round(g.Average(r => r.Seconds)),
                rejected.GroupBy(r => r.Outcome ?? "failed").OrderByDescending(c => c.Count()).ToDictionary(c => c.Key, c => c.Count()));
        }).OrderByDescending(m => m.Checks).ToList();

    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    private static double Round(double seconds) => Math.Round(seconds, 1);
}
