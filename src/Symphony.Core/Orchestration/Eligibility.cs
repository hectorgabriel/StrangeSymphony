using Symphony.Core.Config;
using Symphony.Core.Domain;

namespace Symphony.Core.Orchestration;

/// <summary>Candidate selection and dispatch ordering rules (SPEC 8.2).</summary>
public static class Eligibility
{
    public static bool HasRequiredFields(Issue issue) =>
        !string.IsNullOrWhiteSpace(issue.Id)
        && !string.IsNullOrWhiteSpace(issue.Identifier)
        && !string.IsNullOrWhiteSpace(issue.Title)
        && !string.IsNullOrWhiteSpace(issue.State);

    /// <summary><c>issue_routable</c>: adapter dispatchable plus every required label (case/whitespace-insensitive).</summary>
    public static bool IsRoutable(Issue issue, ServiceConfig config) =>
        issue.Dispatchable && MissingLabels(issue, config).Count == 0;

    /// <summary>Configured required labels the issue lacks (case/whitespace-insensitive).</summary>
    public static IReadOnlyList<string> MissingLabels(Issue issue, ServiceConfig config)
    {
        var labels = issue.Labels.Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
        // A blank configured label matches no issue.
        return config.Tracker.RequiredLabels
            .Where(required => required.Trim().ToLowerInvariant() is var r && (r.Length == 0 || !labels.Contains(r)))
            .ToList();
    }

    public static IEnumerable<Issue> SortForDispatch(IEnumerable<Issue> issues) =>
        issues
            .OrderBy(i => i.Priority is >= 1 and <= 4 ? 0 : 1)
            .ThenBy(i => i.Priority is >= 1 and <= 4 ? i.Priority.Value : 0)
            .ThenBy(i => i.CreatedAt is null ? 1 : 0)
            .ThenBy(i => i.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Identifier, StringComparer.Ordinal);
}
