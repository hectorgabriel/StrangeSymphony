using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.AzureDevOps;

/// <summary>Azure Boards adapter for <c>tracker.kind: azure_devops</c>.</summary>
public sealed class AzureDevOpsAdapter : ITrackerAdapter
{
    private readonly AzureDevOpsOptions _options;
    private readonly TrackerSettings _settings;
    private readonly AzureDevOpsClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private (string? Id, string? Name)? _assignee;

    public AzureDevOpsAdapter(AzureDevOpsOptions options, TrackerSettings settings, HttpMessageHandler handler, ILogger<AzureDevOpsAdapter> logger)
    {
        _options = options;
        _settings = settings;
        _client = new AzureDevOpsClient(options, handler);
        _logger = logger;
    }

    public AzureDevOpsOptions Options => _options;

    // ---- Read kernel (SPEC 11.1) ------------------------------------------------------------

    public async Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct)
    {
        var states = stateNames.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (states.Count == 0)
        {
            return [];
        }

        // Keyset pagination over [System.Id] keeps order stable and avoids the 20k WIQL cap.
        var ids = new List<int>();
        var lastId = 0;
        while (true)
        {
            var page = await _client.QueryIdsAsync(BuildWiql(states, lastId), _options.WiqlPageSize, ct);
            if (page.Count > 0 && page[0] <= lastId)
            {
                throw new TrackerException(TrackerErrorCategories.Pagination, "WIQL page did not advance past the previous id");
            }

            ids.AddRange(page);
            if (page.Count < _options.WiqlPageSize)
            {
                break;
            }

            lastId = page[^1];
        }

        var raws = await LoadRawAsync(ids, ct);
        var result = new List<Issue>(raws.Count);
        var blockerStates = await LoadBlockerStatesAsync(raws, ct);
        foreach (var id in ids)
        {
            if (!raws.TryGetValue(id, out var raw))
            {
                continue; // deleted or no longer readable between query and fetch
            }

            var issue = await NormalizeAsync(raw, blockerStates, ct);
            if (issue is null)
            {
                _logger.LogWarning("action=tracker_normalize outcome=omitted work_item_id={WorkItemId} reason=missing_required_fields", id);
                continue;
            }

            result.Add(issue);
        }

        return result;
    }

    public async Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        var ids = issueIds
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .Where(n => n is > 0)
            .Select(n => n!.Value)
            .Distinct()
            .ToList();

        var raws = await LoadRawAsync(ids, ct);
        var blockerStates = await LoadBlockerStatesAsync(raws, ct);
        var result = new List<Issue>();
        foreach (var raw in raws.Values)
        {
            if (!InScope(raw))
            {
                continue; // moved out of the configured project/area/type scope
            }

            var issue = await NormalizeAsync(raw, blockerStates, ct)
                ?? throw new TrackerException(TrackerErrorCategories.Response, $"work item {raw.Id} is missing required fields (title/state)");
            result.Add(issue);
        }

        return result;
    }

    // ---- Provider-native tools (SPEC 10.5) --------------------------------------------------

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() => AzureDevOpsTools.Specs;

    public IReadOnlyList<string> SecretEnvironmentNames() =>
        AzureDevOpsOptions.KnownSecretEnvNames
            .Append(_options.TokenEnvName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct) =>
        AzureDevOpsTools.ExecuteAsync(_client, name, arguments, issue, ct);

    // ---- Helpers -----------------------------------------------------------------------------

    private string BuildWiql(IReadOnlyList<string> states, int afterId)
    {
        var clauses = new List<string>
        {
            "[System.TeamProject] = @project",
            $"[System.State] IN ({string.Join(", ", states.Select(Quote))})",
            $"[System.Id] > {afterId}",
        };
        if (_options.AreaPath is { } area) clauses.Add($"[System.AreaPath] UNDER {Quote(area)}");
        if (_options.WorkItemTypes.Count > 0) clauses.Add($"[System.WorkItemType] IN ({string.Join(", ", _options.WorkItemTypes.Select(Quote))})");
        return $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", clauses)} ORDER BY [System.Id] ASC";
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private bool InScope(WorkItemNormalizer.Raw raw)
    {
        if (!string.Equals(raw.TeamProject, _options.Project, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_options.AreaPath is { } area
            && !(string.Equals(raw.AreaPath, area, StringComparison.OrdinalIgnoreCase)
                 || raw.AreaPath?.StartsWith(area + "\\", StringComparison.OrdinalIgnoreCase) == true))
        {
            return false;
        }

        return _options.WorkItemTypes.Count == 0
            || _options.WorkItemTypes.Contains(raw.WorkItemType ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<int, WorkItemNormalizer.Raw>> LoadRawAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, WorkItemNormalizer.Raw>();
        foreach (var chunk in ids.Chunk(AzureDevOpsClient.BatchLimit))
        {
            foreach (var item in await _client.GetWorkItemsAsync(chunk, expandRelations: true, fields: null, ct))
            {
                var raw = WorkItemNormalizer.ReadRaw(item)
                    ?? throw new TrackerException(TrackerErrorCategories.Response, "work item payload without a numeric id");
                result[raw.Id] = raw;
            }
        }

        return result;
    }

    private async Task<Dictionary<int, string?>> LoadBlockerStatesAsync(Dictionary<int, WorkItemNormalizer.Raw> raws, CancellationToken ct)
    {
        var predecessorIds = raws.Values.SelectMany(r => r.PredecessorIds).Distinct().ToList();
        var states = new Dictionary<int, string?>();
        foreach (var known in predecessorIds.Where(raws.ContainsKey))
        {
            states[known] = raws[known].State;
        }

        var missing = predecessorIds.Where(id => !states.ContainsKey(id)).ToList();
        foreach (var chunk in missing.Chunk(AzureDevOpsClient.BatchLimit))
        {
            var items = await _client.GetWorkItemsAsync(chunk, expandRelations: false, ["System.Id", "System.State"], ct);
            foreach (var item in items)
            {
                if (WorkItemNormalizer.ReadRaw(item) is { } raw)
                {
                    states[raw.Id] = raw.State;
                }
            }
        }

        return states;
    }

    private async Task<Issue?> NormalizeAsync(WorkItemNormalizer.Raw raw, Dictionary<int, string?> blockerStates, CancellationToken ct)
    {
        // Unreadable predecessors (deleted, other project without access) are dropped, not invented.
        var blockers = raw.PredecessorIds
            .Where(blockerStates.ContainsKey)
            .Select(id => new BlockerRef(
                id.ToString(CultureInfo.InvariantCulture),
                WorkItemNormalizer.Identifier(_options, id),
                blockerStates[id]))
            .ToList();

        var dispatchable = await AssigneeMatchesAsync(raw.Fields, ct)
            && (!_options.BlockOnPredecessors || blockers.All(b => b.State is not null && IsTerminal(b.State)));

        return WorkItemNormalizer.ToIssue(raw, _options, blockers, dispatchable);
    }

    private bool IsTerminal(string state)
    {
        var n = StateNames.Normalize(state);
        return _settings.TerminalStates.Any(t => StateNames.Normalize(t) == n);
    }

    private async Task<bool> AssigneeMatchesAsync(JsonObject fields, CancellationToken ct)
    {
        if (_options.AssignedTo is null)
        {
            return true;
        }

        var (id, name) = await ResolveAssigneeAsync(ct);
        return WorkItemNormalizer.AssigneeMatches(fields, id, name);
    }

    private async Task<(string? Id, string? Name)> ResolveAssigneeAsync(CancellationToken ct)
    {
        if (_assignee is { } cached)
        {
            return cached;
        }

        await _identityGate.WaitAsync(ct);
        try
        {
            if (_assignee is { } again)
            {
                return again;
            }

            var configured = _options.AssignedTo!;
            (string?, string?) resolved;
            if (string.Equals(configured, "@me", StringComparison.OrdinalIgnoreCase))
            {
                var id = await _client.GetAuthenticatedUserIdAsync(ct)
                    ?? throw new TrackerException(TrackerErrorCategories.Response, "connectionData did not return the authenticated user id");
                resolved = (id, null);
            }
            else
            {
                resolved = Guid.TryParse(configured, out _) ? (configured, null) : (null, configured);
            }

            _assignee = resolved;
            return resolved;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _identityGate.Dispose();
    }
}

public sealed class AzureDevOpsAdapterFactory(HttpMessageHandler? handler = null) : ITrackerAdapterFactory
{
    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public string Kind => "azure_devops";

    /// <summary>Terminal defaults cover the Agile, Scrum, Basic and CMMI processes; active states are required.</summary>
    public TrackerStateDefaults StateDefaults { get; } = new(null, ["Done", "Closed", "Removed"]);

    public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env) =>
        AzureDevOpsOptions.Parse(provider, env);

    public ITrackerAdapter Create(TrackerSettings settings, Func<string, string?> env, ILoggerFactory loggerFactory) =>
        new AzureDevOpsAdapter(
            AzureDevOpsOptions.Parse(settings.Provider, env),
            settings,
            handler ?? SharedHandler,
            loggerFactory.CreateLogger<AzureDevOpsAdapter>());
}
