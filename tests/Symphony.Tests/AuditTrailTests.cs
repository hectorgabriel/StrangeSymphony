using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;
using Symphony.Core.Agents;
using Symphony.Core.Audit;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"symphony-audit-{Guid.NewGuid():N}.jsonl");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    public void Dispose() => File.Delete(_path);

    private static AuditRecord Dispatched(string state, DateTimeOffset at, string issue = "1") =>
        new(AuditRecord.Dispatched, AuditRecord.BySymphony, issue, "T" + issue, at, state, Model: "sonnet");

    private static AuditRecord Moved(string from, string to, DateTimeOffset at, bool ok = true) =>
        new(AuditRecord.ToolCall, AuditRecord.ByAgent, "1", "T1", at, to, from, Tool: "update_state", Ok: ok);

    private AuditLog Fill(params AuditRecord[] records)
    {
        var log = new AuditLog(NullLogger.Instance);
        log.Load(_path);
        foreach (var r in records) log.Append(_path, r);
        return log;
    }

    [Fact]
    public void Lines_are_numbered_chained_and_read_back_intact()
    {
        Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5)), Dispatched("Todo", T0, issue: "2"));

        var read = AuditLog.Read(_path, _ => true, 100);

        Assert.True(read.ChainIntact);
        Assert.Equal(3, read.Lines);
        Assert.Equal([1L, 2L, 3L], read.Entries.Select(e => e.Record.Seq));
        Assert.All(read.Entries, e => Assert.True(e.Intact));
        Assert.Equal(new string('0', 64), read.Entries[0].Record.Prev);
        Assert.EndsWith("\"}", File.ReadAllLines(_path)[0], StringComparison.Ordinal);
        Assert.Equal(["T2"], AuditLog.Read(_path, r => r.Identifier == "T2", 100).Entries.Select(e => e.Record.Identifier));
    }

    [Fact]
    public void An_edited_line_breaks_the_chain_there_and_only_there()
    {
        Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5)), Dispatched("Todo", T0.AddMinutes(9)));
        var lines = File.ReadAllLines(_path);
        lines[1] = lines[1].Replace("\"Review\"", "\"Done\"", StringComparison.Ordinal);
        File.WriteAllLines(_path, lines);

        var read = AuditLog.Read(_path, _ => true, 100);

        Assert.False(read.ChainIntact);
        Assert.Equal(2, read.FirstBrokenLine);
        Assert.Equal([true, false, true], read.Entries.Select(e => e.Intact));
    }

    [Fact]
    public void A_removed_line_breaks_the_chain_at_the_line_after_it()
    {
        Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5)), Dispatched("Todo", T0.AddMinutes(9)));
        File.WriteAllLines(_path, File.ReadAllLines(_path).Where((_, i) => i != 1));

        var read = AuditLog.Read(_path, _ => true, 100);

        Assert.Equal(2, read.FirstBrokenLine);
    }

    [Fact]
    public void A_restart_continues_the_chain_and_the_numbering()
    {
        Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5)));
        var again = Fill(Dispatched("Review", T0.AddMinutes(9)));

        var read = AuditLog.Read(_path, _ => true, 100);
        Assert.True(read.ChainIntact);
        Assert.Equal(3, read.Entries[^1].Record.Seq);
        Assert.Equal("Review", again.LastKnownState("1"));
    }

    [Fact]
    public void A_move_symphony_did_not_make_is_reported_as_outside()
    {
        var log = Fill(Dispatched("Todo", T0));
        var issue = TestIssues.Make("1", identifier: "T1", state: "In Progress");

        var change = log.Observe(issue, T0.AddMinutes(1), Grace);

        Assert.NotNull(change);
        Assert.Equal((AuditRecord.StateChanged, AuditRecord.Outside, "Todo", "In Progress"), (change.Kind, change.Actor, change.FromState, change.State));
        Assert.Null(log.Observe(TestIssues.Make("9", state: "Done"), T0.AddMinutes(1), Grace));
    }

    [Fact]
    public void The_agents_own_move_and_stale_reads_are_not_outside_moves()
    {
        var log = Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5)));

        Assert.Null(log.Observe(TestIssues.Make("1", state: "review"), T0.AddMinutes(6), Grace));
        // Read before the move, or a lagging read just after it: still the old state, not a move back.
        Assert.Null(log.Observe(TestIssues.Make("1", state: "Todo"), T0.AddMinutes(4), Grace));
        Assert.Null(log.Observe(TestIssues.Make("1", state: "Todo"), T0.AddMinutes(5).AddSeconds(30), Grace));
        // Well after the move: a person moved it back.
        var back = log.Observe(TestIssues.Make("1", state: "Todo"), T0.AddMinutes(7), Grace);
        Assert.Equal(("Review", "Todo"), (back!.FromState, back.State));
    }

    [Fact]
    public void A_refused_move_does_not_change_the_known_state()
    {
        var log = Fill(Dispatched("Todo", T0), Moved("Todo", "Review", T0.AddMinutes(5), ok: false));

        Assert.Equal("Todo", log.LastKnownState("1"));
        Assert.Null(log.Observe(TestIssues.Make("1", state: "Todo"), T0.AddMinutes(9), Grace));
    }

    [Fact]
    public void Arguments_keep_the_state_and_only_a_fingerprint_of_everything_else()
    {
        var args = AuditLog.SummarizeArgs(new JsonObject { ["state"] = "Por Probar", ["body"] = "Cliente: ACME, contrato 4471", ["n"] = 3 });

        Assert.Equal("Por Probar", args["state"]!.GetValue<string>());
        Assert.Equal(28, args["body"]!["chars"]!.GetValue<int>());
        Assert.Equal(16, args["body"]!["sha256"]!.GetValue<string>().Length);
        Assert.DoesNotContain("ACME", args.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, args["n"]!["chars"]!.GetValue<int>());
    }
}

public class AuditedTrackerTests
{
    private sealed class Tracker(AgentToolResult result) : ITrackerAdapter
    {
        public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);
        public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<Issue>>([]);
        public Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct) => Task.FromResult(result);
        public void Dispose() { }
    }

    private static async Task<HostToolCall> Call(AgentToolResult result, string tool, JsonObject args)
    {
        var events = new List<AgentEvent>();
        var tracker = new AuditedTracker(new Tracker(result), events.Add);
        var returned = await tracker.ExecuteAgentToolAsync(tool, args, TestIssues.Make("1", state: "Plan Aprobado"), default);
        Assert.Same(result, returned);
        var ev = Assert.Single(events);
        Assert.Equal(AgentEventNames.HostToolCall, ev.Event);
        return ev.HostTool!;
    }

    [Fact]
    public async Task A_passed_move_reports_the_move_and_the_gates_commit()
    {
        var output = new JsonObject { ["gate"] = new JsonObject { ["commit"] = "abc1234def", ["branch"] = "feature/x" } };

        var call = await Call(new AgentToolResult(true, output), "update_state", new JsonObject { ["state"] = "Por Probar" });

        Assert.Equal(("update_state", true, "Plan Aprobado", "Por Probar", "abc1234def", "feature/x"),
            (call.Tool, call.Ok, call.FromState, call.State, call.Commit, call.Branch));
    }

    [Fact]
    public async Task A_refused_call_reports_its_category_and_other_tools_their_url()
    {
        var refused = await Call(AgentToolResult.Fail("verification_failed", "tests failed"), "update_state", new JsonObject { ["state"] = "Por Probar" });
        Assert.Equal((false, "verification_failed"), (refused.Ok, refused.Category));

        var pr = await Call(new AgentToolResult(true, new JsonObject { ["url"] = "https://dev.azure.com/pr/7" }), "create_pull_request", new JsonObject { ["title"] = "X" });
        Assert.Equal(("https://dev.azure.com/pr/7", null, null), (pr.Url, pr.State, pr.FromState));
    }
}

public class AuditTrailOrchestratorTests
{
    private static AgentEvent Tool(string tool, string? state, bool ok = true) =>
        new(AgentEventNames.HostToolCall, DateTimeOffset.UtcNow)
        {
            HostTool = new HostToolCall(tool, state is null ? [] : new JsonObject { ["state"] = state }, ok, ok ? null : "verification_failed", state, "Todo", null, null, null),
        };

    [Fact]
    public async Task Records_the_run_the_agents_moves_and_a_persons_move_back()
    {
        await using var h = new OrchestratorHarness();
        h.Tracker.Put(TestIssues.Make("1"));
        h.Start();
        await Wait.Until(() => h.Workers.Length == 1, "dispatch");

        h.Workers[0].Emit(Tool("update_state", "In Progress", ok: false));
        h.Workers[0].Emit(Tool("update_state", "In Progress"));
        h.Workers[0].Emit(Tool("add_comment", null));
        h.Tracker.Put(TestIssues.Make("1", state: "In Progress"));
        await h.Tick();
        // A minute and more later, someone moves it back.
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Tracker.Put(TestIssues.Make("1", state: "Todo"));
        await h.Tick();
        h.Workers[0].Succeed();
        var path = h.Orchestrator.AuditFile;
        await Wait.Until(() => AuditLog.Read(path, r => r.Kind == AuditRecord.RunEnded, 10).Entries.Count == 1, "run end");

        var read = StatusServer.ReadAudit(path, "wi-1", null);
        Assert.True(read.ChainIntact);
        Assert.Equal(
            [
                (AuditRecord.Dispatched, AuditRecord.BySymphony, "Todo"),
                (AuditRecord.ToolCall, AuditRecord.ByAgent, "In Progress"),
                (AuditRecord.ToolCall, AuditRecord.ByAgent, "In Progress"),
                (AuditRecord.ToolCall, AuditRecord.ByAgent, null),
                (AuditRecord.StateChanged, AuditRecord.Outside, "Todo"),
                (AuditRecord.RunEnded, AuditRecord.BySymphony, "Todo"),
            ],
            read.Entries.Select(e => (e.Record.Kind, e.Record.Actor, e.Record.State)));
        var dispatched = read.Entries[0].Record;
        Assert.Equal(dispatched.RunId, read.Entries[^1].Record.RunId);
        Assert.Equal("completed", read.Entries[^1].Record.Outcome);
        Assert.Equal("In Progress", read.Entries[4].Record.FromState);

        var json = StatusServer.AuditJson(read);
        Assert.True(json["chain_intact"]!.GetValue<bool>());
        Assert.Equal("outside", json["entries"]![4]!["actor"]!.GetValue<string>());
        Assert.Equal(5, json["entries"]![4]!["line"]!.GetValue<long>());
    }

    [Fact]
    public async Task Audit_path_puts_the_trail_where_the_workflow_says()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"symphony-audit-dir-{Guid.NewGuid():N}");
        try
        {
            await using var h = new OrchestratorHarness($"audit:\n  path: \"{TempWorkflow.YamlPath(Path.Combine(dir, "trail.jsonl"))}\"");
            h.Tracker.Put(TestIssues.Make("1"));
            h.Start();
            await Wait.Until(() => h.Workers.Length == 1, "dispatch");

            Assert.Equal(Path.Combine(dir, "trail.jsonl"), h.Orchestrator.AuditFile);
            await Wait.Until(() => File.Exists(Path.Combine(dir, "trail.jsonl")), "trail file");
            Assert.False(File.Exists(AuditLog.DefaultPathFor(h.Tmp.WorkspaceRoot)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
