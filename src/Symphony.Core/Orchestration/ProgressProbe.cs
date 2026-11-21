using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.Core.Orchestration;

/// <summary>
/// Tells whether a turn did anything: the workspace's git state (refs, HEAD, the working tree's changes) or a
/// tracker tool call that changes something other than a comment. A turn that only reads the issue and comments
/// "still blocked" is idle. See docs/agent-guardrails.md.
/// </summary>
public static class ProgressProbe
{
    private const int MaxUntrackedFiles = 5000;

    /// <summary>A hash of the workspace's git state, or null when it isn't a git repository (progress unknown).</summary>
    public static async Task<string?> WorkspaceAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(await GitCli.RunAsync(workspacePath, ct, "for-each-ref", "--format=%(refname) %(objectname)")).Append('\0');
            sb.Append(await GitCli.RunAsync(workspacePath, ct, "symbolic-ref", "-q", "HEAD")).Append('\0');
            sb.Append(await GitCli.RunAsync(workspacePath, ct, "status", "--porcelain", "--untracked-files=all")).Append('\0');
            // Edits to a file that was already modified don't change the status, only the diff.
            sb.Append(await GitCli.RunAsync(workspacePath, ct, "diff", "HEAD", "--no-ext-diff", "--binary")).Append('\0');
            // Nor does rewriting a file that is still untracked: its size and write time do change.
            var untracked = await GitCli.RunAsync(workspacePath, ct, "ls-files", "--others", "--exclude-standard", "-z");
            foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(MaxUntrackedFiles))
            {
                var file = new FileInfo(Path.Combine(workspacePath, path));
                if (file.Exists)
                {
                    sb.Append(path).Append(' ').Append(file.Length).Append(' ').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        catch (GitException)
        {
            return null;
        }
    }
}

/// <summary>Counts the session's successful tracker calls that change something other than a comment.</summary>
public sealed class ProgressCountingTracker(ITrackerAdapter inner) : ITrackerAdapter
{
    private const string AddComment = "add_comment";
    private int _changes;

    public int Changes => Volatile.Read(ref _changes);

    public ITrackerAdapter Inner => inner;

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<string> SecretEnvironmentNames() => inner.SecretEnvironmentNames();

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() => inner.AgentToolSpecs();

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        var result = await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        if (result.Success && name != AddComment && inner.AgentToolSpecs().Any(s => s.Name == name && s.Mutates))
        {
            Interlocked.Increment(ref _changes);
        }

        return result;
    }

    public void Dispose()
    {
        // The tracker belongs to the workflow and outlives the session.
    }
}
