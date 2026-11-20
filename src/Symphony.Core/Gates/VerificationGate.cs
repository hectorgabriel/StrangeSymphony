using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.Core.Gates;

/// <summary>What a gate found. <see cref="Category"/> and <see cref="Message"/> are set when it did not pass.</summary>
public sealed record GateOutcome(bool Passed, string? Category, string? Message, string? Commit, string? Branch, int Seconds)
{
    public const string NotOnBranch = "not_on_branch";
    public const string UncommittedChanges = "uncommitted_changes";
    public const string NotPushed = "not_pushed";
    public const string CommandFailed = "verification_failed";
    public const string WorkspaceChanged = "workspace_changed";
    public const string GitError = "git_error";
}

/// <summary>
/// Runs one <see cref="GateSettings"/> in a workspace: first that the work is committed and pushed (when
/// <see cref="GateSettings.RequirePushed"/>), then each <see cref="GateSettings.Run"/> command in order,
/// stopping at the first failure. The agent can't vouch for any of this; the host checks it.
/// </summary>
public static class VerificationGate
{
    private const int MaxListedPaths = 10;

    public static async Task<GateOutcome> CheckAsync(
        GateSettings gate, string workspacePath, string shell, IEnumerable<string> removeEnvironment, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string? branch = null;
        string? head = null;
        GateOutcome Fail(string category, string message) => new(false, category, message, head, branch, Seconds(watch));

        try
        {
            if (gate.RequirePushed)
            {
                branch = (await GitCli.RunAsync(workspacePath, ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim();
                if (branch.Length == 0)
                {
                    branch = null;
                    return Fail(GateOutcome.NotOnBranch, "HEAD is detached; check out the branch you pushed");
                }

                // Untracked files count too: a document written but never added is work the next stage won't see.
                var status = (await GitCli.RunAsync(workspacePath, ct, "status", "--porcelain")).TrimEnd();
                if (status.Length > 0)
                {
                    return Fail(GateOutcome.UncommittedChanges,
                        "the workspace has changes that are not committed; commit and push them, or remove them:\n" + FirstLines(status));
                }

                head = (await GitCli.RunAsync(workspacePath, ct, "rev-parse", "HEAD")).Trim();
                var remoteHead = (await GitCli.RunAsync(workspacePath, ct, "ls-remote", gate.Remote, $"refs/heads/{branch}")).Split('\t', 2)[0].Trim();
                if (remoteHead.Length == 0)
                {
                    return Fail(GateOutcome.NotPushed, $"branch '{branch}' is not on {gate.Remote}; push it first");
                }

                if (!string.Equals(remoteHead, head, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(GateOutcome.NotPushed,
                        $"{gate.Remote}/{branch} is at {GitCli.ShortSha(remoteHead)} but HEAD is {GitCli.ShortSha(head)}; push (or pull) so they match");
                }
            }

            var strip = removeEnvironment.ToList();
            foreach (var command in gate.Run)
            {
                var remaining = gate.TimeoutMs - (int)watch.ElapsedMilliseconds;
                var result = remaining > 0
                    ? await ShellRunner.RunAsync(command, workspacePath, remaining, ct, shell, keepTail: true, removeEnvironment: strip)
                    : new ShellResult(null, true, "");
                if (!result.Succeeded)
                {
                    var how = result.TimedOut
                        ? $"did not finish within the gate's {gate.TimeoutMs / 1000} s"
                        : $"exited with code {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
                    return Fail(GateOutcome.CommandFailed, $"the command {how}.\nCommand: {command}\nOutput (end):\n{result.Output}");
                }
            }

            // What was checked must be what the next stage gets: the commands may not commit or edit tracked files.
            if (gate.RequirePushed && gate.Run.Count > 0)
            {
                var after = (await GitCli.RunAsync(workspacePath, ct, "status", "--porcelain", "--untracked-files=no")).TrimEnd();
                if (after.Length > 0)
                {
                    return Fail(GateOutcome.WorkspaceChanged,
                        "the gate's commands changed tracked files, so what they checked is not what was pushed:\n" + FirstLines(after));
                }

                if (!string.Equals((await GitCli.RunAsync(workspacePath, ct, "rev-parse", "HEAD")).Trim(), head, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(GateOutcome.WorkspaceChanged, "HEAD moved while the gate's commands ran");
                }
            }
        }
        catch (GitException ex)
        {
            return Fail(GateOutcome.GitError, ex.Message);
        }

        return new GateOutcome(true, null, null, head, branch, Seconds(watch));
    }

    private static int Seconds(Stopwatch watch) => (int)Math.Round(watch.Elapsed.TotalSeconds);

    private static string FirstLines(string text)
    {
        var lines = text.Split('\n');
        return string.Join("\n", lines.Take(MaxListedPaths)) + (lines.Length > MaxListedPaths ? $"\n… and {lines.Length - MaxListedPaths} more" : "");
    }
}

/// <summary>
/// The tracker as an agent session sees it when the workflow has <c>gates</c>: <c>update_state</c> to a
/// gated state runs that gate in the workspace first, and the state changes only if it passes. Every
/// other call goes straight through. Does not own <paramref name="inner"/>.
/// </summary>
public sealed class GatedTracker(
    ITrackerAdapter inner,
    IReadOnlyDictionary<string, GateSettings> gates,
    string workspacePath,
    string shell,
    ILogger logger,
    Action<AgentEvent>? onEvent = null,
    IReadOnlyList<string>? stripEnvironment = null) : ITrackerAdapter
{
    /// <summary>The tool every tracker adapter uses to change an issue's state, with its <c>state</c> argument.</summary>
    public const string UpdateStateTool = "update_state";

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken ct) =>
        inner.FetchIssuesByStatesAsync(stateNames, ct);

    public Task<IReadOnlyList<Issue>> FetchIssuesByIdsAsync(IReadOnlyCollection<string> issueIds, CancellationToken ct) =>
        inner.FetchIssuesByIdsAsync(issueIds, ct);

    public IReadOnlyList<string> SecretEnvironmentNames() => inner.SecretEnvironmentNames();

    public IReadOnlyList<AgentToolSpec> AgentToolSpecs() =>
        inner.AgentToolSpecs().Select(s => s.Name == UpdateStateTool ? Gated(s) : s).ToList();

    private AgentToolSpec Gated(AgentToolSpec spec)
    {
        var names = string.Join(", ", gates.Values.Select(g => $"\"{g.State}\""));
        var longest = gates.Values.Max(g => (g.Run.Count > 0 ? g.TimeoutMs : 0) + ConfigBuilder.GateOverheadMs);
        return spec with
        {
            Description = spec.Description
                + $" Moving to {names} first runs Symphony's verification gate in the workspace (work committed and pushed, then the project's checks);"
                + " if it fails, the status is not changed and the result says which check failed and ends with its output. Fix that, push, and call this again.",
            TimeoutMs = Math.Max(spec.TimeoutMs ?? 0, longest),
        };
    }

    public async Task<AgentToolResult> ExecuteAgentToolAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        if (name != UpdateStateTool
            || arguments["state"] is not JsonValue v || !v.TryGetValue(out string? target)
            || !gates.TryGetValue(StateNames.Normalize(target), out var gate))
        {
            return await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        }

        logger.LogInformation("action=gate outcome=running issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} commands={Commands}",
            issue.Id, issue.Identifier, gate.State, gate.Run.Count);
        onEvent?.Invoke(new AgentEvent(AgentEventNames.Notification, DateTimeOffset.UtcNow) { Message = $"gate \"{gate.State}\": running" });
        var outcome = await VerificationGate.CheckAsync(gate, workspacePath, shell, [.. inner.SecretEnvironmentNames(), .. stripEnvironment ?? []], ct);
        void Report(string message) => onEvent?.Invoke(new AgentEvent(AgentEventNames.GateChecked, DateTimeOffset.UtcNow)
        {
            Message = message,
            Gate = new GateCheck(gate.State, issue.State, outcome.Passed, outcome.Category, outcome.Seconds),
        });
        if (!outcome.Passed)
        {
            logger.LogWarning("action=gate outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} category={Category} seconds={Seconds} reason={Reason}",
                issue.Id, issue.Identifier, gate.State, outcome.Category, outcome.Seconds, outcome.Message!.Split('\n')[0]);
            Report($"gate \"{gate.State}\" failed: {outcome.Category}: {outcome.Message!.Split('\n')[0]}");
            return AgentToolResult.Fail(outcome.Category!,
                $"Symphony's verification gate for \"{gate.State}\" did not pass, so the status was not changed: {outcome.Message}");
        }

        logger.LogInformation("action=gate outcome=passed issue_id={IssueId} issue_identifier={IssueIdentifier} state={State} commit={Commit} seconds={Seconds}",
            issue.Id, issue.Identifier, gate.State, outcome.Commit is null ? "-" : GitCli.ShortSha(outcome.Commit), outcome.Seconds);
        Report($"gate \"{gate.State}\" passed in {outcome.Seconds} s");

        var result = await inner.ExecuteAgentToolAsync(name, arguments, issue, ct);
        if (result.Success && result.Output is JsonObject output)
        {
            output["gate"] = new JsonObject
            {
                ["state"] = gate.State,
                ["passed"] = true,
                ["commit"] = outcome.Commit,
                ["branch"] = outcome.Branch,
                ["commands"] = new JsonArray([.. gate.Run.Select(c => (JsonNode?)c)]),
                ["seconds"] = outcome.Seconds,
            };
        }

        return result;
    }

    public void Dispose()
    {
        // The tracker belongs to the workflow and outlives the session.
    }
}
