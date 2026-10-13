using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.Wrike;

/// <summary>
/// Wrike adapter for <c>tracker.kind: wrike</c>. The queue is the set of subtasks of one Wrike task
/// (<c>parent_task_id</c>); states are the names of the queue workflow's custom statuses.
/// </summary>
public sealed class WrikeAdapter : ITrackerAdapter
{
    private readonly WrikeOptions _options;
    private readonly TrackerSettings _settings;
    private readonly WrikeClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string? UpdatedDate, string? Text)> _descriptions = new(StringComparer.Ordinal);
    private volatile Context? _context;

    /// <summary>Resolved once per adapter: the queue task's v4 id, its workflow, and the assignee filter.</summary>
    private sealed record Context(string ParentId, WrikeStatusMap Statuses, string? AssigneeId);

    public WrikeAdapter(WrikeOptions options, TrackerSettings settings, HttpMessageHandler handler, ILogger<WrikeAdapter> logger)
    {
        _options = options;
        _settings = settings;
        _client = new WrikeClient(options, handler);
        _logger = logger;
    }

    public WrikeOptions Options => _options;

    // ---- Read kernel (SPEC 11.1) ------------------------------------------------------------

    public async Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct)
    {
        var wanted = stateNames.Where(s => !string.IsNullOrWhiteSpace(s)).Select(StateNames.Normalize).ToHashSet();
        if (wanted.Count == 0)
        {
            return [];
        }

        var context = await ContextAsync(ct);
        var parent = (await LoadTasksAsync([context.ParentId], ct)).FirstOrDefault()
            ?? throw new TrackerException(TrackerErrorCategories.Response, "the Wrike queue task is no longer visible");
        var tasks = await LoadTasksAsync(WrikeNormalizer.Strings(parent["subTaskIds"]), ct);
        context = await RefreshStatusesAsync(context, tasks, ct);

        var candidates = tasks.Where(t => wanted.Contains(StateNames.Normalize(StateOf(context, t)))).ToList();
        var blockers = await LoadBlockersAsync(context, candidates, tasks, ct);
        var result = new List<Issue>(candidates.Count);
        foreach (var task in candidates)
        {
            var issue = await NormalizeAsync(context, task, blockers, ct);
            if (issue is null)
            {
                _logger.LogWarning("action=tracker_normalize outcome=omitted task_id={TaskId} reason=missing_required_fields", WrikeNormalizer.Str(task["id"]));
                continue;
            }

            result.Add(issue);
        }

        return result;
    }

    public async Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct)
    {
        var ids = issueIds.Where(WrikeOptions.IsTaskId).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var context = await ContextAsync(ct);
        // A task that left the queue (no longer a subtask of the queue task) is out of scope.
        var tasks = (await LoadTasksAsync(ids, ct))
            .Where(t => WrikeNormalizer.Strings(t["superTaskIds"]).Contains(context.ParentId))
            .ToList();
        context = await RefreshStatusesAsync(context, tasks, ct);

        var blockers = await LoadBlockersAsync(context, tasks, tasks, ct);
        var result = new List<Issue>(tasks.Count);
        foreach (var task in tasks)
        {
            result.Add(await NormalizeAsync(context, task, blockers, ct)
                ?? throw new TrackerException(TrackerErrorCategories.Response,
                    $"Wrike task {WrikeNormalizer.Str(task["id"])} is missing required fields (title/status)"));
        }

        return result;
    }

    // ---- Provider-native tools (SPEC 10.5) --------------------------------------------------

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() => WrikeTools.Specs;

    public IReadOnlyList<string> SecretEnvironmentNames() =>
        WrikeOptions.KnownSecretEnvNames
            .Append(_options.TokenEnvName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        Context context;
        try
        {
            context = await ContextAsync(ct);
        }
        catch (TrackerException ex)
        {
            return AgentToolResult.Fail(ex.Category, ex.Message);
        }

        return await WrikeTools.ExecuteAsync(_client, context.Statuses, _options, name, arguments, issue, ct);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private async Task<Context> ContextAsync(CancellationToken ct)
    {
        if (_context is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_context is { } again)
            {
                return again;
            }

            var parentId = _options.ParentIsPermalinkId
                ? await _client.ResolvePermalinkIdAsync(_options.ParentTask, ct)
                : _options.ParentTask;
            var parent = parentId is null ? null : (await LoadTasksAsync([parentId], ct)).FirstOrDefault();
            if (parent is null || parentId is null)
            {
                throw new TrackerException(TrackerErrorCategories.InvalidTrackerConfig,
                    $"tracker.provider.parent_task_id '{_options.ParentTask}' is not a Wrike task visible to the token");
            }

            var statuses = WrikeStatusMap.Build(await _client.GetWorkflowsAsync(ct), _options.Workflow, WrikeNormalizer.Str(parent["customStatusId"]));
            foreach (var state in _settings.ActiveStates.Concat(_settings.TerminalStates))
            {
                if (!statuses.TryResolve(state, out _, out var error) && !statuses.Exists(state))
                {
                    throw new TrackerException(TrackerErrorCategories.InvalidTrackerConfig, $"tracker state {error}");
                }
            }

            string? assignee = null;
            if (string.Equals(_options.AssignedTo, "@me", StringComparison.OrdinalIgnoreCase))
            {
                assignee = await _client.GetMyContactIdAsync(ct)
                    ?? throw new TrackerException(TrackerErrorCategories.Response, "contacts?me=true did not return the token's user");
            }
            else if (_options.AssignedTo is { } contact)
            {
                assignee = contact;
            }

            _logger.LogInformation("action=tracker_context outcome=resolved parent_task_id={ParentId} workflow={Workflow}", parentId, statuses.WorkflowName);
            return _context = new Context(parentId, statuses, assignee);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reloads workflows once when a task carries a status id that was added after the map was built.</summary>
    private async Task<Context> RefreshStatusesAsync(Context context, IReadOnlyList<JsonObject> tasks, CancellationToken ct)
    {
        if (tasks.All(t => context.Statuses.ById(WrikeNormalizer.Str(t["customStatusId"])) is not null))
        {
            return context;
        }

        var statuses = WrikeStatusMap.Build(await _client.GetWorkflowsAsync(ct), _options.Workflow ?? context.Statuses.WorkflowId, null);
        return _context = context with { Statuses = statuses };
    }

    private static string? StateOf(Context context, JsonObject task) =>
        context.Statuses.ById(WrikeNormalizer.Str(task["customStatusId"]))?.Name;

    /// <summary>Batch get that skips ids Wrike reports as invalid (deleted or not visible) instead of failing them all.</summary>
    private async Task<List<JsonObject>> LoadTasksAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var result = new List<JsonObject>(ids.Count);
        foreach (var chunk in ids.Chunk(WrikeClient.BatchLimit))
        {
            try
            {
                result.AddRange(await _client.GetTasksAsync(chunk, ct));
                continue;
            }
            catch (TrackerException ex) when (WrikeClient.IsInvalidId(ex) && chunk.Length > 1)
            {
            }
            catch (TrackerException ex) when (WrikeClient.IsInvalidId(ex))
            {
                continue;
            }

            foreach (var id in chunk)
            {
                try
                {
                    result.AddRange(await _client.GetTasksAsync([id], ct));
                }
                catch (TrackerException ex) when (WrikeClient.IsInvalidId(ex))
                {
                    _logger.LogDebug("action=tracker_fetch outcome=skipped task_id={TaskId} reason=invalid_id", id);
                }
            }
        }

        return result;
    }

    /// <summary>Predecessors per successor task id, read from each task's dependencies.</summary>
    private async Task<Dictionary<string, List<BlockerRef>>> LoadBlockersAsync(
        Context context, IReadOnlyList<JsonObject> tasks, IReadOnlyList<JsonObject> known, CancellationToken ct)
    {
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (WrikeNormalizer.Str(task["id"]) is not { } id || WrikeNormalizer.Strings(task["dependencyIds"]).Count == 0)
            {
                continue;
            }

            predecessors[id] = (await _client.GetDependenciesAsync(id, ct))
                .Where(d => WrikeNormalizer.Str(d["successorId"]) == id)
                .Select(d => WrikeNormalizer.Str(d["predecessorId"]))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var byId = known
            .Where(t => WrikeNormalizer.Str(t["id"]) is not null)
            .GroupBy(t => WrikeNormalizer.Str(t["id"])!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var missing = predecessors.Values.SelectMany(p => p).Distinct(StringComparer.Ordinal).Where(p => !byId.ContainsKey(p)).ToList();
        foreach (var task in await LoadTasksAsync(missing, ct))
        {
            if (WrikeNormalizer.Str(task["id"]) is { } id)
            {
                byId[id] = task;
            }
        }

        context = await RefreshStatusesAsync(context, byId.Values.ToList(), ct);

        // Unreadable predecessors (deleted, no access) are dropped, not invented.
        return predecessors.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Where(byId.ContainsKey)
                .Select(p => new BlockerRef(p, WrikeNormalizer.Identifier(_options, byId[p]), StateOf(context, byId[p])))
                .ToList(),
            StringComparer.Ordinal);
    }

    private async Task<Issue?> NormalizeAsync(Context context, JsonObject task, Dictionary<string, List<BlockerRef>> blockers, CancellationToken ct)
    {
        var id = WrikeNormalizer.Str(task["id"]);
        var taskBlockers = id is not null && blockers.TryGetValue(id, out var b) ? b : [];
        var assigned = context.AssigneeId is null || WrikeNormalizer.Strings(task["responsibleIds"]).Contains(context.AssigneeId);
        var dispatchable = assigned
            && (!_options.BlockOnPredecessors || taskBlockers.All(x => x.State is not null && IsTerminal(x.State)));

        var description = id is null ? null : await DescriptionAsync(id, task, ct);
        return WrikeNormalizer.ToIssue(task, StateOf(context, task), description, _options, context.ParentId, taskBlockers, dispatchable);
    }

    /// <summary>
    /// A batch get omits descriptions (a one-id get includes them), so each one is fetched once per task revision.
    /// </summary>
    private async Task<string?> DescriptionAsync(string id, JsonObject task, CancellationToken ct)
    {
        var updatedDate = WrikeNormalizer.Str(task["updatedDate"]);
        if (task.ContainsKey("description"))
        {
            var inline = Html.ToText(WrikeNormalizer.Str(task["description"]));
            _descriptions[id] = (updatedDate, inline);
            return inline;
        }

        if (_descriptions.TryGetValue(id, out var cached) && cached.UpdatedDate == updatedDate)
        {
            return cached.Text;
        }

        var full = await _client.GetTaskAsync(id, ct);
        var text = Html.ToText(WrikeNormalizer.Str(full?["description"]));
        _descriptions[id] = (updatedDate, text);
        return text;
    }

    private bool IsTerminal(string state)
    {
        var n = StateNames.Normalize(state);
        return _settings.TerminalStates.Any(t => StateNames.Normalize(t) == n);
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}

public sealed class WrikeAdapterFactory(HttpMessageHandler? handler = null) : ITrackerAdapterFactory
{
    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public string Kind => "wrike";

    /// <summary>Terminal defaults are the standard Wrike status names; active states are required.</summary>
    public TrackerStateDefaults StateDefaults { get; } = new(null, ["Completed", "Cancelled"]);

    public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env) =>
        WrikeOptions.Parse(provider, env);

    public ITrackerAdapter Create(TrackerSettings settings, Func<string, string?> env, ILoggerFactory loggerFactory) =>
        new WrikeAdapter(
            WrikeOptions.Parse(settings.Provider, env),
            settings,
            handler ?? SharedHandler,
            loggerFactory.CreateLogger<WrikeAdapter>());
}
