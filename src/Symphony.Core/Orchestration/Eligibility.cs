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
    public static bool IsRoutable(Issue issue, ServiceConfig config)
    {
        if (!issue.Dispatchable)
        {
            return false;
        }

        var labels = issue.Labels.Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
        foreach (var required in config.Tracker.RequiredLabels)
        {
            var r = required.Trim().ToLowerInvariant();
            // A blank configured label matches no issue.
            if (r.Length == 0 || !labels.Contains(r))
            {
                return false;
            }
        }

        return true;
    }

    public static IEnumerable<Issue> SortForDispatch(IEnumerable<Issue> issues) =>
        issues
            .OrderBy(i => i.Priority is >= 1 and <= 4 ? 0 : 1)
            .ThenBy(i => i.Priority is >= 1 and <= 4 ? i.Priority.Value : 0)
            .ThenBy(i => i.CreatedAt is null ? 1 : 0)
            .ThenBy(i => i.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Identifier, StringComparer.Ordinal);
}
