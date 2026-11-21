using Symphony.Core.Agents;
using Symphony.Core.Config;

namespace Symphony.Copilot;

/// <summary>
/// Builds one GitHub Copilot CLI prompt-mode run (<c>copilot --output-format json</c>, prompt on stdin). A
/// session is one process per turn: the first creates it with <c>--session-id</c>, later turns use <c>--resume</c>.
/// </summary>
public static class CopilotCommandLine
{
    /// <summary>Copilot names a tracker tool <c>tracker-&lt;tool&gt;</c>; this rule allows all of them.</summary>
    public const string TrackerToolRule = McpConfigFile.TrackerServerName;

    public static IReadOnlyList<string> BuildArgs(
        CopilotSettings settings,
        string? model,
        string sessionId,
        bool resume,
        string? mcpConfigPath,
        bool hasTrackerTools,
        string usagePath,
        IReadOnlyList<string> secretEnvNames)
    {
        var args = new List<string>
        {
            // Prompt mode reads the prompt from stdin, so no prompt length limit on the command line.
            "--output-format", "json",
            // No question tool, and anything that would prompt for permission is denied (SPEC 10.5 "never stall").
            "--no-ask-user",
            resume ? $"--resume={sessionId}" : $"--session-id={sessionId}",
            // Agent sessions stay on this machine; the history is not copied to the GitHub account.
            "--sync-session=off",
            // github-mcp-server and the other built-ins: the agent gets the tracker and nothing else by default.
            "--disable-builtin-mcps",
            $"--usage-output-file={usagePath}",
        };

        if (model is not null)
        {
            args.Add($"--model={model}");
        }

        if (settings.MaxAiCredits is { } credits)
        {
            // Copilot's own cap for the session; the workflow's token budgets still apply on top.
            args.Add($"--max-ai-credits={credits}");
        }

        if (settings.ReasoningEffort is { } effort)
        {
            args.Add($"--reasoning-effort={effort}");
        }

        // The =value form: these flags take several values and would swallow the next argument otherwise.
        args.AddRange(settings.AllowTools.Select(t => $"--allow-tool={t}"));
        if (hasTrackerTools)
        {
            args.Add($"--allow-tool={TrackerToolRule}");
        }

        args.AddRange(settings.DenyTools.Select(t => $"--deny-tool={t}"));
        args.AddRange(settings.ExcludedTools.Select(t => $"--excluded-tools={t}"));

        if (mcpConfigPath is not null)
        {
            args.Add($"--additional-mcp-config=@{mcpConfigPath}");
        }

        if (secretEnvNames.Count > 0)
        {
            args.Add($"--secret-env-vars={string.Join(",", secretEnvNames)}");
        }

        args.AddRange(settings.ExtraArgs);
        return args;
    }

    /// <summary>
    /// Environment changes for the CLI. Copilot reads a GitHub token from these before its own login, and a
    /// classic <c>ghp_</c> token there makes every run fail; <c>COPILOT_GITHUB_TOKEN</c> is kept, as the explicit
    /// way to sign Copilot in with a token.
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysStripped =
        ["GH_TOKEN", "GITHUB_TOKEN", "CLAUDECODE", "COPILOT_CLI", "COPILOT_AGENT_SESSION_ID", "COPILOT_ALLOW_ALL"];

    public static IReadOnlyDictionary<string, string> FixedEnvironment(string home) => new Dictionary<string, string>
    {
        ["COPILOT_HOME"] = home,
        // By default the CLI reads `bash -l` once and copies its exports into the agent's shell, which would bring
        // back variables Symphony removed (tracker secrets, workspace.strip_env).
        ["COPILOT_DISABLE_LOGIN_SHELL_ENV"] = "1",
        // By default --resume changes to the directory the session was recorded in, not the workspace.
        ["COPILOT_DISABLE_RESUME_AUTO_CD"] = "1",
        ["COPILOT_AUTO_UPDATE"] = "false",
    };
}
