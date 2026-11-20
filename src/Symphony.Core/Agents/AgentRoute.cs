namespace Symphony.Core.Agents;

/// <summary>The coding agents Symphony can run (<c>agent.runner</c>). Each has a front matter section of the same name.</summary>
public static class AgentRunners
{
    public const string Claude = "claude";
    public const string Copilot = "copilot";

    public static readonly IReadOnlyList<string> All = [Claude, Copilot];

    /// <summary>A saved or configured runner name; missing means <see cref="Claude"/>, which ran every session before runners existed.</summary>
    public static string Normalize(string? runner) =>
        string.IsNullOrWhiteSpace(runner) ? Claude : runner.Trim().ToLowerInvariant();
}

/// <summary>
/// Which agent runs a session, and with which model (null: the runner's default). A session keeps its route:
/// when the issue moves to a state routed elsewhere, the session ends and the next one starts with the new route.
/// </summary>
public sealed record AgentRoute(string Runner, string? Model);
