using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Symphony.Core.Orchestration;

/// <summary>
/// Scheduler state that survives a restart (SPEC 18.2 extension): the retry queue, the runs that were live,
/// the per-issue counters and the board's handoff rows. Tracker state stays the source of truth; every
/// restored entry goes through the normal retry refresh before anything is dispatched.
/// </summary>
public sealed record OrchestratorState(
    int Version,
    DateTimeOffset SavedAt,
    IReadOnlyList<SavedIssue> Issues,
    IReadOnlyList<HandoffRow> Handoffs,
    SavedUsage? Usage = null,
    IReadOnlyList<SavedWait>? Waiting = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// An issue parked after an idle session (agent.max_idle_turns). <see cref="Baseline"/> is the tracker's
/// <c>UpdatedAt</c> after Symphony's comment; a different one wakes the issue. Not known yet means the next poll sets it.
/// </summary>
public sealed record SavedWait(
    string IssueId,
    string Identifier,
    string? Url,
    string? State,
    DateTimeOffset Since,
    string? Reason,
    DateTimeOffset? Baseline,
    bool BaselineKnown);

/// <summary>
/// One claimed issue. <see cref="Kind"/> is <c>retry</c> for a queued retry or continuation and
/// <c>interrupted</c> for a run that was live when the process stopped.
/// </summary>
public sealed record SavedIssue(
    string IssueId,
    string Identifier,
    string? Url,
    string Kind,
    int? Attempt,
    DateTimeOffset? DueAt,
    string? Error,
    string? State,
    string? SessionId,
    string? WorkspacePath,
    int RestartCount,
    string? LastError,
    int CleanExitsInState,
    string? CleanExitState,
    // The agent session to continue, with the state, model and runner it ran in (the runner's resume setting).
    // A missing runner means claude: state files from before agent.runner existed.
    string? ResumeSessionId = null,
    string? ResumeState = null,
    string? ResumeModel = null,
    string? ResumeRunner = null)
{
    public const string Retry = "retry";
    public const string Interrupted = "interrupted";
}

/// <summary>Tokens used on <see cref="Day"/> (host-local date, <c>yyyy-MM-dd</c>), for <c>budget.max_tokens_per_day</c>.</summary>
public sealed record SavedUsage(string Day, long Tokens);

/// <summary>Reads and atomically writes <see cref="OrchestratorState"/> next to the workspaces.</summary>
public sealed class OrchestratorStateFile(ILogger logger)
{
    public const string FileName = ".symphony-state.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private string? _lastWritten;
    private string? _lastWrittenPath;
    private string? _lastError;

    public static string PathFor(string workspaceRoot) => Path.Combine(Path.GetFullPath(workspaceRoot), FileName);

    /// <summary>The saved state, or null when there is none or it can't be used; a bad file is logged and ignored.</summary>
    public OrchestratorState? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<OrchestratorState>(text, Json);
            if (state is null || state.Version != OrchestratorState.CurrentVersion)
            {
                logger.LogWarning("action=state_load outcome=ignored path={Path} reason=unsupported_version version={Version}", path, state?.Version);
                return null;
            }

            state = state with { Issues = state.Issues ?? [], Handoffs = state.Handoffs ?? [] };
            _lastWritten = JsonSerializer.Serialize(state with { SavedAt = default }, Json);
            _lastWrittenPath = path;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning("action=state_load outcome=ignored path={Path} reason={Reason}", path, ex.Message);
            return null;
        }
    }

    /// <summary>Writes only when the content changed. A failed write is logged once per distinct error and never throws.</summary>
    public void Save(string path, OrchestratorState state)
    {
        // SavedAt alone never justifies a write.
        var text = JsonSerializer.Serialize(state with { SavedAt = default }, Json);
        if (text == _lastWritten && path == _lastWrittenPath)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, path, overwrite: true);
            _lastWritten = text;
            _lastWrittenPath = path;
            _lastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message != _lastError)
            {
                _lastError = ex.Message;
                logger.LogWarning("action=state_save outcome=failed path={Path} reason={Reason} note=continuing_in_memory", path, ex.Message);
            }
        }
    }
}
