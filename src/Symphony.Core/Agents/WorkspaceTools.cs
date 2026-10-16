using System.Text.Json.Nodes;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Core.Agents;

/// <summary>
/// Host-side tools bound to one agent session's workspace, served next to the tracker's tools.
/// Like those, they run with credentials the agent never sees.
/// </summary>
public interface IAgentToolSet : IDisposable
{
    IReadOnlyList<AgentToolSpec> Specs { get; }

    IReadOnlyList<string> SecretEnvironmentNames { get; }

    Task<AgentToolResult> ExecuteAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct);
}

/// <summary>Builds the workspace tools for one session, or none when the workflow doesn't configure any.</summary>
public interface IWorkspaceToolsFactory
{
    IAgentToolSet? Create(ServiceConfig config, string workspacePath, Issue issue);
}

/// <summary>
/// The tracker adapter as the agent session sees it: the same reads, plus the workspace tools. It owns
/// only <paramref name="extra"/>; the tracker belongs to the workflow and outlives the session.
/// </summary>
public sealed class ToolAugmentedTracker : ITrackerAdapter
{
    private readonly ITrackerAdapter _inner;
    private readonly IAgentToolSet _extra;
    private readonly HashSet<string> _extraNames;

    public ToolAugmentedTracker(ITrackerAdapter inner, IAgentToolSet extra)
    {
        _inner = inner;
        _extra = extra;
        _extraNames = extra.Specs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (inner.AgentToolSpecs().FirstOrDefault(s => _extraNames.Contains(s.Name)) is { } clash)
        {
            throw new InvalidOperationException($"workspace tool '{clash.Name}' has the same name as a tracker tool");
        }
    }

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        _inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        _inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() => [.. _inner.AgentToolSpecs(), .. _extra.Specs];

    public IReadOnlyList<string> SecretEnvironmentNames() =>
        _inner.SecretEnvironmentNames().Concat(_extra.SecretEnvironmentNames).Distinct(StringComparer.Ordinal).ToList();

    public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct) =>
        _extraNames.Contains(name)
            ? _extra.ExecuteAsync(name, arguments, issue, ct)
            : _inner.ExecuteAgentToolAsync(name, arguments, issue, ct);

    public void Dispose() => _extra.Dispose();
}
