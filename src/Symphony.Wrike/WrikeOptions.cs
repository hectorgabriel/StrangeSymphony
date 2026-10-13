using System.Globalization;
using System.Text.RegularExpressions;
using Symphony.Core.Config;
using Symphony.Core.Tracker;

namespace Symphony.Wrike;

/// <summary>
/// Resolved <c>tracker.provider</c> settings for <c>tracker.kind: wrike</c>.
/// See docs/adapters/wrike.md for the published adapter profile.
/// </summary>
public sealed partial record WrikeOptions
{
    /// <summary>Host-side fallback env var for an omitted <c>token</c>.</summary>
    public const string DefaultTokenEnv = "WRIKE_TOKEN";

    /// <summary>Credential env names stripped from agent child processes in addition to the configured one.</summary>
    public static readonly string[] KnownSecretEnvNames = ["WRIKE_TOKEN", "WRIKE_ACCESS_TOKEN"];

    public required string Host { get; init; }
    public required string Token { get; init; }
    public string? TokenEnvName { get; init; }

    /// <summary>The queue task as configured: a v4 task id or a numeric permalink id.</summary>
    public required string ParentTask { get; init; }
    public string? Workflow { get; init; }
    public string? AssignedTo { get; init; }
    public string IdentifierPrefix { get; init; } = "WR";
    public bool BlockOnPredecessors { get; init; } = true;
    public int RequestTimeoutMs { get; init; } = 30000;

    public Uri ApiBase => new($"https://{Host}/api/v4/");

    /// <summary>True when <see cref="ParentTask"/> is a permalink id that must be converted to a v4 id.</summary>
    public bool ParentIsPermalinkId => ParentTask.All(char.IsAsciiDigit);

    public static WrikeOptions Parse(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
    {
        var (hostRaw, _) = ConfigValues.ResolveEnvReference(Str(provider, "host"), env);
        var host = string.IsNullOrWhiteSpace(hostRaw) ? "www.wrike.com" : HostOf(hostRaw.Trim());
        // The token is sent to this host, so only Wrike's own hosts are accepted.
        if (host is null || !(host == "wrike.com" || host.EndsWith(".wrike.com", StringComparison.Ordinal)))
        {
            throw Invalid("tracker.provider.host must be a wrike.com host (for example www.wrike.com or app-eu.wrike.com)");
        }

        var (parentRaw, _) = ConfigValues.ResolveEnvReference(Str(provider, "parent_task_id"), env);
        var parent = ParentTaskId(parentRaw)
            ?? throw Invalid("tracker.provider.parent_task_id is required: the queue task's id, permalink id, or permalink URL");

        // Secret: explicit $VAR, literal (discouraged), or the documented host-side fallback env.
        var tokenRaw = Str(provider, "token");
        string? token;
        string? tokenEnv;
        if (tokenRaw is null)
        {
            tokenEnv = DefaultTokenEnv;
            token = env(DefaultTokenEnv);
        }
        else
        {
            (token, tokenEnv) = ConfigValues.ResolveEnvReference(tokenRaw, env);
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new TrackerException(TrackerErrorCategories.MissingTrackerSecret,
                tokenEnv is null
                    ? "tracker.provider.token is empty"
                    : $"tracker.provider.token: environment variable {tokenEnv} is unset or empty");
        }

        var (workflow, _) = ConfigValues.ResolveEnvReference(Str(provider, "workflow"), env);
        var (assignedTo, _) = ConfigValues.ResolveEnvReference(Str(provider, "assigned_to"), env);

        return new WrikeOptions
        {
            Host = host,
            Token = token,
            TokenEnvName = tokenEnv,
            ParentTask = parent,
            Workflow = string.IsNullOrWhiteSpace(workflow) ? null : workflow.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim(),
            IdentifierPrefix = Str(provider, "identifier_prefix") is { Length: > 0 } prefix ? prefix.Trim() : "WR",
            BlockOnPredecessors = Bool(provider, "block_on_predecessors", true),
            RequestTimeoutMs = Int(provider, "request_timeout_ms", 30000, 1000),
        };
    }

    /// <summary>Accepts a v4 id, a numeric permalink id, or a permalink URL (<c>…open.htm?id=123</c>).</summary>
    internal static string? ParentTaskId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return PermalinkId(uri.ToString());
        }

        return TaskId().IsMatch(value) ? value : null;
    }

    /// <summary>The numeric id in a Wrike permalink, or null.</summary>
    public static string? PermalinkId(string? permalink)
    {
        if (permalink is null) return null;
        var m = PermalinkIdPattern().Match(permalink);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Wrike ids are short alphanumeric tokens that may contain <c>-</c> or <c>_</c>; anything else never reaches a URL path.</summary>
    public static bool IsTaskId(string? id) => id is not null && TaskId().IsMatch(id);

    private static string? HostOf(string value)
    {
        var withScheme = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/"
            ? uri.IdnHost.ToLowerInvariant()
            : null;
    }

    private static string? Str(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is not null
            ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

    private static bool Bool(IReadOnlyDictionary<string, object?> map, string key, bool fallback) =>
        !map.TryGetValue(key, out var v) || v is null ? fallback
        : v is bool b ? b
        : v is string s && bool.TryParse(s, out var parsed) ? parsed
        : throw Invalid($"tracker.provider.{key} must be a boolean");

    private static int Int(IReadOnlyDictionary<string, object?> map, string key, int fallback, int min)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return fallback;
        var ok = v switch
        {
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => (int?)null,
        };
        return ok is { } n && n >= min ? n : throw Invalid($"tracker.provider.{key} must be an integer >= {min}");
    }

    private static TrackerException Invalid(string message) => new(TrackerErrorCategories.InvalidTrackerConfig, message);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TaskId();

    [GeneratedRegex(@"[?&]id=(\d+)")]
    private static partial Regex PermalinkIdPattern();
}
