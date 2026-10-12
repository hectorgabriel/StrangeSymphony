using Symphony.Core.Domain;

namespace Symphony.Tests;

internal static class TestIssues
{
    public static Issue Make(
        string id,
        string? identifier = null,
        string state = "Todo",
        int? priority = null,
        DateTimeOffset? createdAt = null,
        bool dispatchable = true,
        IReadOnlyList<string>? labels = null) => new()
    {
        Id = id,
        Identifier = identifier ?? $"WI-{id}",
        Title = $"Title {id}",
        State = state,
        Priority = priority,
        CreatedAt = createdAt,
        Dispatchable = dispatchable,
        Labels = (labels ?? []).Select(l => l.Trim().ToLowerInvariant()).ToList(),
    };
}
