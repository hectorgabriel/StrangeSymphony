using System.Globalization;
using Symphony.Core.Config;
using Symphony.Core.Tracker;

namespace Symphony.AzureDevOps;

/// <summary>
/// Resolved <c>tracker.provider</c> settings for <c>tracker.kind: azure_devops</c>.
/// See docs/adapters/azure-devops.md for the published adapter profile.
/// </summary>
public sealed record AzureDevOpsOptions
{
    /// <summary>Host-side fallback env var for an omitted <c>token</c>.</summary>
    public const string DefaultTokenEnv = "AZURE_DEVOPS_PAT";

    /// <summary>Credential env names stripped from agent child processes in addition to the configured one.</summary>
    public static readonly string[] KnownSecretEnvNames = ["AZURE_DEVOPS_PAT", "AZURE_DEVOPS_EXT_PAT", "SYSTEM_ACCESSTOKEN", "AZURE_DEVOPS_TOKEN"];

    public required Uri OrganizationUrl { get; init; }
    public required string Project { get; init; }
    public required string Token { get; init; }
    public string? TokenEnvName { get; init; }
    public string? AssignedTo { get; init; }
    public string? AreaPath { get; init; }
    public IReadOnlyList<string> WorkItemTypes { get; init; } = [];
    public string IdentifierPrefix { get; init; } = "WI";
    public bool BlockOnPredecessors { get; init; } = true;
    public string ApiVersion { get; init; } = "7.1";
    public int RequestTimeoutMs { get; init; } = 30000;
    public int WiqlPageSize { get; init; } = 1000;

    public static AzureDevOpsOptions Parse(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
    {
        var orgRaw = Str(provider, "organization_url");
        var (org, _) = ConfigValues.ResolveEnvReference(orgRaw, env);
        if (string.IsNullOrWhiteSpace(org))
        {
            throw Invalid("tracker.provider.organization_url is required (for example https://dev.azure.com/contoso)");
        }

        if (!Uri.TryCreate(org.TrimEnd('/'), UriKind.Absolute, out var orgUri) || (orgUri.Scheme != Uri.UriSchemeHttps && orgUri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid("tracker.provider.organization_url must be an absolute http(s) URL");
        }

        var (project, _) = ConfigValues.ResolveEnvReference(Str(provider, "project"), env);
        if (string.IsNullOrWhiteSpace(project))
        {
            throw Invalid("tracker.provider.project is required");
        }

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

        var (assignedTo, _) = ConfigValues.ResolveEnvReference(Str(provider, "assigned_to"), env);
        var (areaPath, _) = ConfigValues.ResolveEnvReference(Str(provider, "area_path"), env);

        return new AzureDevOpsOptions
        {
            OrganizationUrl = new Uri(orgUri.ToString().TrimEnd('/') + "/"),
            Project = project.Trim(),
            Token = token,
            TokenEnvName = tokenEnv,
            AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim(),
            AreaPath = string.IsNullOrWhiteSpace(areaPath) ? null : areaPath.Trim(),
            WorkItemTypes = List(provider, "work_item_types"),
            IdentifierPrefix = Str(provider, "identifier_prefix") is { Length: > 0 } prefix ? prefix.Trim() : "WI",
            BlockOnPredecessors = Bool(provider, "block_on_predecessors", true),
            ApiVersion = Str(provider, "api_version") ?? "7.1",
            RequestTimeoutMs = Int(provider, "request_timeout_ms", 30000, 1000),
            WiqlPageSize = Int(provider, "wiql_page_size", 1000, 1),
        };
    }

    private static string? Str(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is not null
            ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

    private static List<string> List(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return [];
        if (v is not List<object?> list) throw Invalid($"tracker.provider.{key} must be a list of strings");
        return list.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)?.Trim() ?? "").Where(x => x.Length > 0).ToList();
    }

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
}
