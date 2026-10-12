using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

/// <summary>In-memory tracker shared by every adapter instance the factory creates.</summary>
public sealed class FakeTracker : ITrackerAdapter
{
    public ConcurrentDictionary<string, Issue> Issues { get; } = new();
    public int StateFetches;
    public int IdFetches;
    public bool FailIdFetches { get; set; }
    public bool FailStateFetches { get; set; }
    /// <summary>When set, failures are rate limits carrying this Retry-After.</summary>
    public int? RetryAfterMs { get; set; }
    public int Disposals;
    /// <summary>One-shot: the next id fetch captures its result, then waits for this gate.</summary>
    public TaskCompletionSource? NextIdFetchGate;
    public List<(string Tool, JsonObject Args, string IssueId)> ToolCalls { get; } = [];

    public void Put(Issue issue) => Issues[issue.Id] = issue;

    private TrackerException Failure() => RetryAfterMs is { } ms
        ? new TrackerException(TrackerErrorCategories.RateLimited, "slow down") { RetryAfterMs = ms }
        : new TrackerException(TrackerErrorCategories.Request, "boom");

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct)
    {
        Interlocked.Increment(ref StateFetches);
        if (FailStateFetches) throw Failure();
        var states = stateNames.Select(StateNames.Normalize).ToHashSet();
        return Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.Where(i => states.Contains(StateNames.Normalize(i.State))).ToList());
    }

    public async Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct)
    {
        Interlocked.Increment(ref IdFetches);
        if (FailIdFetches) throw Failure();
        var result = issueIds.Where(Issues.ContainsKey).Select(id => Issues[id]).ToList();
        if (Interlocked.Exchange(ref NextIdFetchGate, null) is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return result;
    }

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
        [new AgentToolSpec("echo", "echo", new JsonObject { ["type"] = "object" }, Mutates: false)];

    public IReadOnlyList<string> SecretEnvironmentNames() => ["FAKE_TRACKER_TOKEN"];

    public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        lock (ToolCalls) ToolCalls.Add((name, arguments, issue.Id));
        return Task.FromResult(name == "echo"
            ? new AgentToolResult(true, new JsonObject { ["issue"] = issue.Identifier, ["args"] = arguments.DeepClone() })
            : AgentToolResult.Fail("unsupported_tool", name));
    }

    public void Dispose() => Interlocked.Increment(ref Disposals);
}

public sealed class FakeTrackerFactory(FakeTracker tracker) : ITrackerAdapterFactory
{
    public string Kind => "fake";
    public TrackerStateDefaults StateDefaults { get; } = new(["Todo", "In Progress"], ["Done", "Cancelled"]);

    public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
    {
        if (provider.TryGetValue("fail", out var f) && f is true)
        {
            throw new TrackerException(TrackerErrorCategories.InvalidTrackerConfig, "fake provider rejected");
        }
    }

    public ITrackerAdapter Create(TrackerSettings settings, Func<string, string?> env, ILoggerFactory loggerFactory) => tracker;
}

/// <summary>Creates a new adapter per snapshot, like real factories do.</summary>
public sealed class PerSnapshotTrackerFactory(Action<FakeTracker>? seed = null) : ITrackerAdapterFactory
{
    public List<FakeTracker> Created { get; } = [];
    public string Kind => "fake";
    public TrackerStateDefaults StateDefaults { get; } = new(["Todo", "In Progress"], ["Done", "Cancelled"]);

    public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
    {
    }

    public ITrackerAdapter Create(TrackerSettings settings, Func<string, string?> env, ILoggerFactory loggerFactory)
    {
        var tracker = new FakeTracker();
        seed?.Invoke(tracker);
        lock (Created) Created.Add(tracker);
        return tracker;
    }
}

/// <summary>Temp directory with a WORKFLOW.md, cleaned up on dispose.</summary>
public sealed class TempWorkflow : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("symphony-test-").FullName;
    public string Path => System.IO.Path.Combine(Dir, "WORKFLOW.md");
    public string WorkspaceRoot => System.IO.Path.Combine(Dir, "ws");

    public TempWorkflow(string frontMatter, string body = "Work on {{ issue.identifier }}")
    {
        Write(frontMatter, body);
    }

    public void Write(string frontMatter, string body = "Work on {{ issue.identifier }}") =>
        File.WriteAllText(Path, $"---\n{frontMatter.Replace("{ROOT}", WorkspaceRoot)}\n---\n{body}\n");

    public WorkflowStore Store(FakeTracker tracker) =>
        new(Path, new ConfigBuilder([new FakeTrackerFactory(tracker)], _ => null), _ => null, NullLoggerFactory.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
