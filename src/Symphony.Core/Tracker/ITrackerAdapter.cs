using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Domain;

namespace Symphony.Core.Tracker;

/// <summary>Portable adapter error categories (SPEC 11.4).</summary>
public static class TrackerErrorCategories
{
    public const string UnsupportedTrackerKind = "unsupported_tracker_kind";
    public const string InvalidTrackerConfig = "invalid_tracker_config";
    public const string MissingTrackerSecret = "missing_tracker_secret";
    public const string Request = "tracker_request";
    public const string Status = "tracker_status";
    public const string Response = "tracker_response";
    public const string Pagination = "tracker_pagination";
    public const string RateLimited = "tracker_rate_limited";
}

/// <summary>
/// The language-native adapter error form. Every adapter failure surfaces as this exception with a
/// stable <see cref="Category"/> and human-readable message (SPEC 11.1, 11.4).
/// </summary>
public sealed class TrackerException(string category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Category { get; } = category;
    public int? ProviderStatus { get; init; }
    public int? RetryAfterMs { get; init; }
    public override string ToString() => $"{Category}: {Message}";
}

public sealed record AgentToolSpec(string Name, string Description, JsonObject InputSchema, bool Mutates);

/// <summary>Tool outcome; <see cref="Output"/> is JSON-safe (SPEC 10.5).</summary>
public sealed record AgentToolResult(bool Success, JsonNode? Output)
{
    public static AgentToolResult Fail(string category, string message) =>
        new(false, new JsonObject { ["error"] = new JsonObject { ["category"] = category, ["message"] = message } });
}

/// <summary>Read kernel plus OPTIONAL provider-native tools (SPEC 11.1, 10.5).</summary>
public interface ITrackerAdapter : IDisposable
{
    Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct);

    Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct);

    IReadOnlyList<AgentToolSpec> AgentToolSpecs() => [];

    /// <summary>Environment names that hold tracker credentials; stripped from agent child processes.</summary>
    IReadOnlyList<string> SecretEnvironmentNames() => [];

    Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct) =>
        Task.FromResult(AgentToolResult.Fail("unsupported_tool", $"unsupported tool: {name}"));
}

public sealed record TrackerStateDefaults(IReadOnlyList<string>? ActiveStates, IReadOnlyList<string>? TerminalStates);

/// <summary>Builds adapters for one <c>tracker.kind</c>.</summary>
public interface ITrackerAdapterFactory
{
    string Kind { get; }

    TrackerStateDefaults StateDefaults { get; }

    /// <summary>Validates <c>tracker.provider</c> after defaults and <c>$VAR</c> resolution; throws <see cref="TrackerException"/>.</summary>
    void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env);

    ITrackerAdapter Create(TrackerSettings settings, Func<string, string?> env, ILoggerFactory loggerFactory);
}
