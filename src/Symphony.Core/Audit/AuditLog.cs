using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Symphony.Core.Domain;

namespace Symphony.Core.Audit;

/// <summary>
/// One line of the audit trail (docs/audit-trail.md). <see cref="Actor"/> says who acted: <c>symphony</c> (the
/// orchestrator), <c>agent</c> (a tool call from an agent session) or <c>outside</c> (a change Symphony saw in the
/// tracker but did not make: a person or another integration).
/// </summary>
public sealed record AuditRecord(
    string Kind,
    string Actor,
    string IssueId,
    string Identifier,
    DateTimeOffset At,
    // The issue's state: the target of a move, the state a run started in, or the one observed.
    string? State = null,
    string? FromState = null,
    string? RunId = null,
    string? SessionId = null,
    string? Model = null,
    int? Attempt = null,
    bool Resumed = false,
    string? Tool = null,
    // Tool arguments, summarized: `state` verbatim, every other value as its length and a hash prefix.
    JsonObject? Args = null,
    bool? Ok = null,
    string? Category = null,
    string? Commit = null,
    string? Branch = null,
    string? Url = null,
    string? Outcome = null,
    string? Error = null,
    // Why Symphony released or closed the issue, in words.
    string? Reason = null,
    int Turns = 0,
    long TotalTokens = 0,
    double Seconds = 0)
{
    public const string Dispatched = "dispatched";
    public const string ToolCall = "tool_call";
    public const string RunEnded = "run_ended";
    public const string StateChanged = "state_changed";
    public const string Released = "released";
    public const string Closed = "closed";

    public const string BySymphony = "symphony";
    public const string ByAgent = "agent";
    public const string Outside = "outside";

    // Set by the log when it writes the line.
    public long Seq { get; init; }
    public string? Prev { get; init; }
}

/// <summary>An audit line read back, with whether its hash and link to the line before it hold.</summary>
public sealed record AuditEntry(long Line, AuditRecord Record, bool Intact);

/// <summary>What <see cref="AuditLog.Read"/> found: the matching entries and the state of the whole chain.</summary>
public sealed record AuditReadResult(IReadOnlyList<AuditEntry> Entries, int Lines, bool ChainIntact, long? FirstBrokenLine, string? Error);

/// <summary>
/// Append-only JSONL audit trail. Each line carries <c>prev</c>, the hash of the line before, and ends with
/// <c>hash</c>, the SHA-256 of the line without it. Editing, removing or reordering a line breaks the chain from
/// there on, which <see cref="Read"/> reports. It makes changes visible; it does not stop someone with write access
/// from rewriting the whole file, so keep the file where agents can't write (<c>audit.path</c>).
/// </summary>
public sealed class AuditLog(ILogger logger)
{
    public const string FileName = ".symphony-audit.jsonl";
    private const string HashKey = ",\"hash\":\"";
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    // Issue id -> the state Symphony last knew, when and from which state it got there (see Observe).
    private readonly Dictionary<string, KnownState> _known = new();
    private long _seq;
    private string _lastHash = Genesis;
    private string? _lastError;

    private sealed record KnownState(string State, DateTimeOffset At, string? From);

    public static string DefaultPathFor(string workspaceRoot) => Path.Combine(Path.GetFullPath(workspaceRoot), FileName);

    /// <summary>Picks up the chain's end and the last known state of each issue, so a restart continues both.</summary>
    public void Load(string path)
    {
        var read = Read(path, _ => true, int.MaxValue);
        lock (_gate)
        {
            _known.Clear();
            _seq = 0;
            foreach (var e in read.Entries)
            {
                Apply(e.Record);
                _seq = Math.Max(_seq, e.Record.Seq);
            }

            _lastHash = LastHash(path) ?? Genesis;
        }

        if (read.Error is not null)
        {
            logger.LogWarning("action=audit_load outcome=failed path={Path} reason={Reason}", path, read.Error);
        }
        else if (!read.ChainIntact)
        {
            logger.LogWarning("action=audit_load outcome=chain_broken path={Path} first_broken_line={Line} note=the_file_was_changed_outside_symphony;new_lines_continue_from_its_end",
                path, read.FirstBrokenLine);
        }
        else
        {
            logger.LogInformation("action=audit_load outcome=completed path={Path} lines={Lines}", path, read.Lines);
        }
    }

    public void Append(string path, AuditRecord record)
    {
        lock (_gate)
        {
            var line = record with { Seq = _seq + 1, Prev = _lastHash };
            var body = JsonSerializer.Serialize(line, Json);
            var hash = Sha256(body);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, body[..^1] + HashKey + hash + "\"}\n");
                _lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex.Message != _lastError)
                {
                    _lastError = ex.Message;
                    logger.LogError("action=audit_save outcome=failed path={Path} reason={Reason}", path, ex.Message);
                }

                return;
            }

            _seq = line.Seq;
            _lastHash = hash;
            Apply(line);
        }
    }

    /// <summary>
    /// The tracker showed <paramref name="issue"/> in its current state in a fetch that started at
    /// <paramref name="fetchedAt"/>. If that differs from what Symphony last knew, someone outside Symphony moved it:
    /// returns that change to record, or null. Issues Symphony never worked on are not followed. A fetch older than
    /// Symphony's last change, or one still showing the state before it shortly after, is a stale read, not a move.
    /// </summary>
    public AuditRecord? Observe(Issue issue, DateTimeOffset fetchedAt, TimeSpan staleReadGrace)
    {
        lock (_gate)
        {
            if (!_known.TryGetValue(issue.Id, out var known) || Same(known.State, issue.State) || fetchedAt < known.At)
            {
                return null;
            }

            if (known.From is not null && Same(known.From, issue.State) && fetchedAt < known.At + staleReadGrace)
            {
                return null;
            }

            return new AuditRecord(AuditRecord.StateChanged, AuditRecord.Outside, issue.Id, issue.Identifier, fetchedAt, issue.State, known.State);
        }
    }

    public string? LastKnownState(string issueId)
    {
        lock (_gate)
        {
            return _known.TryGetValue(issueId, out var k) ? k.State : null;
        }
    }

    private void Apply(AuditRecord r)
    {
        var moved = r.Kind switch
        {
            AuditRecord.ToolCall => r.Ok == true && r.Tool == "update_state" && r.State is not null,
            AuditRecord.Dispatched or AuditRecord.StateChanged or AuditRecord.Released or AuditRecord.Closed => r.State is not null,
            _ => false,
        };
        if (moved)
        {
            var from = _known.TryGetValue(r.IssueId, out var k) ? k.State : r.FromState;
            _known[r.IssueId] = new KnownState(r.State!, r.At, Same(from, r.State) ? k?.From : from);
        }
    }

    private static bool Same(string? a, string? b) => StateNames.Normalize(a) == StateNames.Normalize(b);

    /// <summary>
    /// Reads the trail, checking every line's hash and its link to the line before. Returns the last
    /// <paramref name="limit"/> entries that match <paramref name="filter"/>, oldest first.
    /// </summary>
    public static AuditReadResult Read(string path, Func<AuditRecord, bool> filter, int limit)
    {
        var entries = new List<AuditEntry>();
        var prev = Genesis;
        long lineNo = 0;
        long? broken = null;
        try
        {
            if (!File.Exists(path))
            {
                return new AuditReadResult([], 0, true, null, null);
            }

            foreach (var text in File.ReadLines(path))
            {
                lineNo++;
                if (text.Length == 0)
                {
                    continue;
                }

                var (record, hash, intact) = Parse(text, prev);
                if (!intact)
                {
                    broken ??= lineNo;
                }

                // After a break, keep checking each line against the one before it, so a single edit shows once.
                prev = hash ?? prev;
                if (record is not null && filter(record))
                {
                    entries.Add(new AuditEntry(lineNo, record, intact));
                    if (entries.Count > limit)
                    {
                        entries.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AuditReadResult(entries, (int)lineNo, broken is null, broken, ex.Message);
        }

        return new AuditReadResult(entries, (int)lineNo, broken is null, broken, null);
    }

    private static (AuditRecord? Record, string? Hash, bool Intact) Parse(string text, string prev)
    {
        var at = text.LastIndexOf(HashKey, StringComparison.Ordinal);
        if (at < 0 || !text.EndsWith("\"}", StringComparison.Ordinal))
        {
            return (TryDeserialize(text), null, false);
        }

        var body = text[..at] + "}";
        var hash = text[(at + HashKey.Length)..^2];
        var record = TryDeserialize(body);
        var intact = record is not null && Sha256(body) == hash && record.Prev == prev;
        return (record, hash, intact);
    }

    private static AuditRecord? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditRecord>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? LastHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var last = File.ReadLines(path).LastOrDefault(l => l.Length > 0);
        if (last is null)
        {
            return null;
        }

        var at = last.LastIndexOf(HashKey, StringComparison.Ordinal);
        return at < 0 || !last.EndsWith("\"}", StringComparison.Ordinal) ? Sha256(last) : last[(at + HashKey.Length)..^2];
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Tool arguments as the trail keeps them: <c>state</c> verbatim, because it is the decision being audited;
    /// every other value only as its length and the start of its SHA-256, which matches it to what the tracker
    /// shows without copying comment text or other content into the trail.
    /// </summary>
    public static JsonObject SummarizeArgs(JsonObject arguments)
    {
        var summary = new JsonObject();
        foreach (var (name, value) in arguments)
        {
            if (name == "state" && value is JsonValue v && v.TryGetValue(out string? state))
            {
                summary[name] = state;
                continue;
            }

            var text = value is JsonValue sv && sv.TryGetValue(out string? s) ? s : value?.ToJsonString() ?? "null";
            summary[name] = new JsonObject { ["chars"] = text.Length, ["sha256"] = Sha256(text)[..16] };
        }

        return summary;
    }
}
