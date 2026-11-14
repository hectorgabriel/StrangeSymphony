using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Orchestration;

namespace Symphony.Tests;

public class OrchestratorStateTests
{
    private static string StatePath(OrchestratorHarness h) => OrchestratorStateFile.PathFor(h.Tmp.WorkspaceRoot);

    [Fact]
    public async Task A_run_interrupted_by_a_restart_comes_back_as_the_next_attempt()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Assert.Null(h.Workers[0].Attempt);

        // The state is saved after the dispatch is handled, which can be after the worker has started.
        await Wait.Until(() => File.Exists(StatePath(h)) && File.ReadAllText(StatePath(h)).Contains("\"interrupted\""), "state saved");
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(StatePath(h)))!;
        Assert.Equal("interrupted", (string?)saved["issues"]![0]!["kind"]);

        await h.RestartAsync();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "restored retry");

        // The new process claims it, so a poll can't start it fresh as attempt null.
        await h.Tick();
        Assert.Single(h.Workers);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "re-dispatch");
        Assert.Equal(1, h.Workers[1].Attempt);
        var detail = h.Snap.Issues["WI-1"];
        Assert.Equal(1, detail.RestartCount);
        Assert.Contains("interrupted", detail.LastError);
    }

    [Fact]
    public async Task A_queued_retry_keeps_its_attempt_and_remaining_backoff()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Workers[0].Fail("kaboom");
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "retry");
        var due = h.Snap.Retrying[0].DueAt;

        h.Time.Advance(TimeSpan.FromSeconds(4));
        await h.RestartAsync();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "restored retry");
        var restored = h.Snap.Retrying[0];
        Assert.Equal((1, due), (restored.Attempt, restored.DueAt));
        Assert.Contains("kaboom", restored.Error);

        h.Time.Advance(TimeSpan.FromMilliseconds(5999));
        await Task.Delay(100);
        Assert.Single(h.Workers);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "retry dispatch");
        Assert.Equal(1, h.Workers[1].Attempt);
    }

    [Fact]
    public async Task The_no_progress_counter_survives_a_restart()
    {
        await using var h = new OrchestratorHarness("agent:\n  max_fast_continuations: 1");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "fast continuation");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Workers.Length == 2, "second session");
        h.Workers[1].Succeed();
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "slowed continuation");
        Assert.Equal(h.Time.GetUtcNow().AddSeconds(10), h.Snap.Retrying[0].DueAt);

        await h.RestartAsync();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => h.Workers.Length == 3, "third session");
        h.Workers[2].Succeed();

        // Third clean exit in the same state: the backoff keeps growing instead of starting over.
        await Wait.Until(() => h.Snap.Retrying.Count == 1, "continuation after restart");
        Assert.Equal(h.Time.GetUtcNow().AddSeconds(20), h.Snap.Retrying[0].DueAt);
    }

    [Fact]
    public async Task Handoffs_survive_a_restart_and_released_issues_stay_released()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        h.Tracker.Put(TestIssues.Make("1", state: "Human Review"));
        await h.Tick();
        await Wait.Until(() => h.Snap.Handoffs.Count == 1 && h.Snap.Running.Count == 0, "handoff");

        await h.RestartAsync();
        Assert.Equal(("WI-1", "Human Review"), (h.Snap.Handoffs.Single().IssueIdentifier, h.Snap.Handoffs[0].HandoffState));
        Assert.Empty(h.Snap.Retrying);
        await h.Tick();
        Assert.Single(h.Workers);
    }

    [Fact]
    public async Task A_restored_retry_for_an_issue_that_closed_meanwhile_is_released()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Tracker.Put(TestIssues.Make("1", state: "Done"));
        await h.RestartAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => h.Snap.Handoffs.Count == 1, "released on refresh");
        Assert.Empty(h.Snap.Retrying);
        Assert.Single(h.Workers);
    }

    [Fact]
    public async Task An_unreadable_state_file_is_ignored_and_replaced()
    {
        await using var h = new OrchestratorHarness();
        Directory.CreateDirectory(h.Tmp.WorkspaceRoot);
        await File.WriteAllTextAsync(StatePath(h), "{ not json");
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();

        await Wait.Until(() => h.Workers.Length == 1, "dispatch");
        Assert.Null(h.Workers[0].Attempt);
        await Wait.Until(() => File.ReadAllText(StatePath(h)).Contains("\"interrupted\"", StringComparison.Ordinal), "rewritten");
    }

    [Fact]
    public void State_file_ignores_other_versions_and_writes_only_on_change()
    {
        using var tmp = new TempWorkflow("tracker:\n  kind: fake");
        Directory.CreateDirectory(tmp.WorkspaceRoot);
        var path = OrchestratorStateFile.PathFor(tmp.WorkspaceRoot);
        var file = new OrchestratorStateFile(NullLogger.Instance);

        File.WriteAllText(path, "{\"version\": 99, \"issues\": [], \"handoffs\": []}");
        Assert.Null(file.Load(path));

        var state = new OrchestratorState(OrchestratorState.CurrentVersion, DateTimeOffset.UnixEpoch, [], []);
        file.Save(path, state);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddMinutes(-5));
        file.Save(path, state with { SavedAt = DateTimeOffset.UnixEpoch.AddHours(1) });
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(path + ".tmp"));

        var loaded = new OrchestratorStateFile(NullLogger.Instance).Load(path);
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Issues);
    }
}
