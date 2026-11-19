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
    string? FromState = null)
{
    public const string Run = "run";
    public const string Handoff = "handoff";
    public const string Closed = "closed";
    public const string Gate = "gate";
    public const string GatePassed = "passed";
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

public sealed record ModelMetrics(string Model, int Runs, long InputTokens, long OutputTokens, long TotalTokens, double AgentSeconds);

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
    int GateRejections = 0);

public sealed record MetricsReport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? Since,
    MetricsTotals Totals,
    IReadOnlyList<StateMetrics> ByState,
    IReadOnlyList<ModelMetrics> ByModel,
    IReadOnlyList<DayMetrics> ByDay,
    IReadOnlyList<IssueMetrics> Issues,
    BudgetStatus? Budget,
    IReadOnlyList<GateMetrics>? ByGate = null);

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
                rejectionsByIssue.GetValueOrDefault(g.Key));
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

        var byModel = runs.GroupBy(r => r.Model ?? DefaultModel)
            .Select(g => new ModelMetrics(g.Key, g.Count(), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.TotalTokens), Round(g.Sum(r => r.Seconds))))
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
            ByGate(gateChecks));
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
