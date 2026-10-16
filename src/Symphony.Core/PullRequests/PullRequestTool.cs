using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;
using Symphony.Core.Util;

namespace Symphony.Core.PullRequests;

/// <summary>
/// <c>create_pull_request</c>: opens a pull request for the workspace's current branch, but only after
/// the host has checked that the branch is committed and pushed, that its diff carries no build output,
/// and that <see cref="PullRequestSettings.VerifyCommand"/> succeeds on exactly that commit. The agent
/// can't skip a step or vouch for one; it only gets the reason back when one fails.
/// </summary>
public sealed class PullRequestTool(
    PullRequestSettings settings,
    IPullRequestProvider provider,
    string workspacePath,
    string shell,
    ILogger<PullRequestTool> logger) : IAgentToolSet
{
    public const string Name = "create_pull_request";

    /// <summary>Time allowed for the checks and the provider calls on top of the verify command.</summary>
    public const int OverheadMs = 180000;

    private const int GitTimeoutMs = 120000;
    private const int MaxListedPaths = 10;

    public IReadOnlyList<AgentToolSpec> Specs =>
    [
        new(Name,
            $"Open a pull request from the current branch into '{settings.TargetBranch}'. Commit and push first. "
            + "The host then checks that the workspace is clean, that the pushed branch matches HEAD and that the diff has no build output, "
            + "and runs the project's verification build on that commit; the pull request is created only if every check passes. "
            + "On failure the result says which check failed and, for the build, ends with its output. "
            + "Calling it again for a branch that already has an open pull request returns that one.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Pull request title." },
                    ["description"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Pull request body (Markdown): what changed, and what a reviewer must check because you could not verify it. The host appends the verification result.",
                    },
                },
                ["required"] = new JsonArray("title", "description"),
                ["additionalProperties"] = false,
            },
            Mutates: true)
        {
            TimeoutMs = settings.VerifyTimeoutMs + OverheadMs,
        },
    ];

    public IReadOnlyList<string> SecretEnvironmentNames => provider.SecretEnvironmentNames;

    public async Task<AgentToolResult> ExecuteAsync(string name, JsonObject arguments, Issue issue, CancellationToken ct)
    {
        if (name != Name)
        {
            return AgentToolResult.Fail("unsupported_tool", $"unsupported tool: {name}");
        }

        var title = Str(arguments["title"])?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return AgentToolResult.Fail("invalid_arguments", "title is required");
        }

        var description = Str(arguments["description"]) ?? "";
        try
        {
            return await CreateAsync(title, description, issue, ct);
        }
        catch (CheckFailed ex)
        {
            logger.LogWarning("action=pull_request outcome=refused issue_identifier={IssueIdentifier} category={Category} reason={Reason}",
                issue.Identifier, ex.Category, ex.Message.Split('\n')[0]);
            return AgentToolResult.Fail(ex.Category, ex.Message);
        }
        catch (PullRequestException ex)
        {
            return AgentToolResult.Fail(ex.Category, ex.Message);
        }
    }

    private async Task<AgentToolResult> CreateAsync(string title, string description, Issue issue, CancellationToken ct)
    {
        var target = settings.TargetBranch;
        var remote = settings.Remote;

        var branch = (await GitAsync(ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim();
        if (branch.Length == 0)
        {
            throw new CheckFailed("not_on_branch", "HEAD is detached; switch to the branch you pushed");
        }

        if (string.Equals(branch, target, StringComparison.Ordinal))
        {
            throw new CheckFailed("invalid_branch", $"the current branch is the target branch '{target}'; work on a feature branch");
        }

        await EnsureCleanAsync(ct);
        var head = (await GitAsync(ct, "rev-parse", "HEAD")).Trim();

        var remoteHead = (await GitAsync(ct, "ls-remote", remote, $"refs/heads/{branch}")).Split('\t', 2)[0].Trim();
        if (remoteHead.Length == 0)
        {
            throw new CheckFailed("not_pushed", $"branch '{branch}' is not on {remote}; push it first");
        }

        if (!string.Equals(remoteHead, head, StringComparison.OrdinalIgnoreCase))
        {
            throw new CheckFailed("not_pushed", $"{remote}/{branch} is at {Short(remoteHead)} but HEAD is {Short(head)}; push (or pull) so they match");
        }

        // An explicit refspec, because a single-branch or shallow clone has no remote-tracking ref for the target.
        await GitAsync(ct, "fetch", "--quiet", remote, $"+refs/heads/{target}:refs/remotes/{remote}/{target}");
        string mergeBase;
        try
        {
            mergeBase = (await GitAsync(ct, "merge-base", "HEAD", $"{remote}/{target}")).Trim();
        }
        catch (CheckFailed ex)
        {
            throw new CheckFailed("no_merge_base",
                $"no common history with {remote}/{target}; in a shallow clone run 'git fetch --deepen=200 {remote}' and try again ({ex.Message})");
        }

        var files = (await GitAsync(ct, "diff", "--name-only", mergeBase, "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (files.Length == 0)
        {
            throw new CheckFailed("no_changes", $"the branch has no changes against {target}");
        }

        var forbidden = files.Where(f => ForbiddenPattern(f, settings.ForbiddenPaths) is not null).ToList();
        if (forbidden.Count > 0)
        {
            throw new CheckFailed("forbidden_paths",
                $"{forbidden.Count} changed file(s) match pull_request.forbidden_paths ({string.Join(", ", settings.ForbiddenPaths)}); "
                + $"remove them from the branch's commits. First ones:\n{string.Join("\n", forbidden.Take(MaxListedPaths))}");
        }

        if (files.Length > settings.MaxFiles)
        {
            throw new CheckFailed("too_many_files",
                $"the branch changes {files.Length} files, more than pull_request.max_files ({settings.MaxFiles}); check that only intended files were committed");
        }

        logger.LogInformation("action=pull_request outcome=verifying issue_identifier={IssueIdentifier} branch={Branch} commit={Commit} files={Files}",
            issue.Identifier, branch, Short(head), files.Length);
        var watch = Stopwatch.StartNew();
        var verify = await ShellRunner.RunAsync(settings.VerifyCommand, workspacePath, settings.VerifyTimeoutMs, ct, shell, keepTail: true);
        watch.Stop();
        var seconds = (int)Math.Round(watch.Elapsed.TotalSeconds);
        if (!verify.Succeeded)
        {
            var how = verify.TimedOut
                ? $"did not finish within {settings.VerifyTimeoutMs / 1000} s"
                : $"exited with code {verify.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
            throw new CheckFailed("verification_failed",
                $"the verification command {how} after {seconds} s, so no pull request was created. Fix the build, commit, push and call this tool again.\n"
                + $"Command: {settings.VerifyCommand}\nOutput (end):\n{verify.Output}");
        }

        // The build must not have moved HEAD or touched tracked files, or what was verified isn't what gets reviewed.
        await EnsureCleanAsync(ct, "after the verification command ran");
        if (!string.Equals((await GitAsync(ct, "rev-parse", "HEAD")).Trim(), head, StringComparison.OrdinalIgnoreCase))
        {
            throw new CheckFailed("workspace_changed", "HEAD moved while the verification command ran; call the tool again");
        }

        logger.LogInformation("action=pull_request outcome=verified issue_identifier={IssueIdentifier} commit={Commit} seconds={Seconds}",
            issue.Identifier, Short(head), seconds);

        var existing = await provider.FindActiveAsync(branch, target, ct);
        if (existing is not null)
        {
            return Result(existing, created: false, head, files.Length, seconds);
        }

        var footer = $"""


            ---
            Verified by Symphony before opening this pull request, for {issue.Identifier}:
            - Commit: `{Short(head)}` on `{branch}`
            - Command: `{settings.VerifyCommand}` exited 0 in {seconds} s
            - Changed files: {files.Length}
            """;
        var room = Math.Max(0, provider.MaxDescriptionLength - footer.Length);
        var body = (description.Length > room ? description[..Math.Max(0, room - 1)] + "…" : description) + footer;
        var created = await provider.CreateAsync(new PullRequestDraft(branch, target, title, body, settings.Draft), ct);
        logger.LogInformation("action=pull_request outcome=created issue_identifier={IssueIdentifier} pull_request={Id} draft={Draft}",
            issue.Identifier, created.Id, created.IsDraft);
        return Result(created, created: true, head, files.Length, seconds);
    }

    private static AgentToolResult Result(PullRequestInfo pr, bool created, string head, int files, int seconds) =>
        new(true, new JsonObject
        {
            ["created"] = created,
            ["id"] = pr.Id,
            ["url"] = pr.Url,
            ["is_draft"] = pr.IsDraft,
            ["verified_commit"] = head,
            ["changed_files"] = files,
            ["verify_seconds"] = seconds,
        });

    private async Task EnsureCleanAsync(CancellationToken ct, string? when = null)
    {
        var status = (await GitAsync(ct, "status", "--porcelain", "--untracked-files=no")).Trim();
        if (status.Length > 0)
        {
            var lines = status.Split('\n');
            throw new CheckFailed("uncommitted_changes",
                $"tracked files have uncommitted changes{(when is null ? "" : " " + when)}; commit and push them, or restore them:\n"
                + string.Join("\n", lines.Take(MaxListedPaths)));
        }
    }

    /// <summary>
    /// The pattern a changed path matches, if any. <c>dir/</c> matches that directory at any depth
    /// (<c>bin/</c> catches <c>src/App/bin/Debug/x.dll</c>); <c>*.ext</c> matches by extension; anything
    /// else matches the file name exactly. Case-insensitive, because Windows checkouts are.
    /// </summary>
    public static string? ForbiddenPattern(string path, IReadOnlyList<string> patterns)
    {
        var normalized = "/" + path.Replace('\\', '/').TrimStart('/');
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0) continue;
            var matches = pattern.EndsWith('/')
                ? normalized.Contains("/" + pattern.TrimStart('/'), StringComparison.OrdinalIgnoreCase)
                : pattern.StartsWith("*.", StringComparison.Ordinal)
                    ? normalized.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                    : normalized.EndsWith("/" + pattern.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
            if (matches) return raw;
        }

        return null;
    }

    private async Task<string> GitAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        // Unattended: a credential prompt must fail, not wait.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CheckFailed("git_error", $"could not start git: {ex.Message}");
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ShellRunner.KillTree(process);
            ct.ThrowIfCancellationRequested();
            throw new CheckFailed("git_error", $"git {args[0]} did not finish within {GitTimeoutMs / 1000} s");
        }

        var output = await stdout;
        var error = (await stderr).Trim();
        if (process.ExitCode != 0)
        {
            // symbolic-ref -q exits 1 with no output on a detached HEAD; callers treat empty output as the answer.
            if (args is ["symbolic-ref", ..] && error.Length == 0) return "";
            throw new CheckFailed("git_error", $"git {string.Join(' ', args)} failed ({process.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
        }

        return output;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public void Dispose() => provider.Dispose();

    private sealed class CheckFailed(string category, string message) : Exception(message)
    {
        public string Category { get; } = category;
    }
}

/// <summary>Adds <c>create_pull_request</c> to sessions whose workflow has a <c>pull_request</c> section.</summary>
public sealed class PullRequestToolFactory(
    IEnumerable<IPullRequestProviderFactory> factories,
    Func<string, string?> env,
    ILoggerFactory loggerFactory) : IWorkspaceToolsFactory
{
    private readonly Dictionary<string, IPullRequestProviderFactory> _factories =
        factories.ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);

    public IAgentToolSet? Create(ServiceConfig config, string workspacePath, Issue issue)
    {
        if (config.PullRequest is not { } settings)
        {
            return null;
        }

        // ConfigBuilder already rejected unknown kinds, so a miss here means the factory list changed.
        var factory = _factories.TryGetValue(settings.Kind, out var f)
            ? f
            : throw new PullRequestException(PullRequestErrorCategories.InvalidConfig, $"pull_request.kind '{settings.Kind}' is not supported");
        return new PullRequestTool(settings, factory.Create(settings, env), workspacePath, config.Hooks.Shell, loggerFactory.CreateLogger<PullRequestTool>());
    }
}
