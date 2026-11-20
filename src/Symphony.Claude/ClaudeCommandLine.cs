using System.Diagnostics;
using System.Text;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Util;

namespace Symphony.Claude;

/// <summary>Builds the Claude Code headless invocation (stream-json in/out, one live process per worker).</summary>
public static class ClaudeCommandLine
{
    public const string TrackerMcpServerName = McpConfigFile.TrackerServerName;

    public static IReadOnlyList<string> BuildArgs(ClaudeSettings settings, string? model, string sessionId, string? mcpConfigPath, bool hasTrackerTools, string title, bool resume = false)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            // --resume continues the session under the same id; --session-id starts a new one with it.
            resume ? "--resume" : "--session-id", sessionId,
            "--permission-mode", settings.PermissionMode,
            // Headless: anything that would prompt is denied instead of waiting (SPEC 10.5 "never stall").
            "--permission-prompts", "none",
            "--name", title,
        };

        // The session's route decides the model (ClaudeSettings.ModelFor for the dispatch state).
        if (model is not null)
        {
            args.Add("--model");
            args.Add(model);
        }

        var allowed = settings.AllowedTools.ToList();
        if (hasTrackerTools)
        {
            allowed.Add($"mcp__{TrackerMcpServerName}");
        }

        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }

        if (settings.DisallowedTools.Count > 0)
        {
            args.Add("--disallowedTools");
            args.AddRange(settings.DisallowedTools);
        }

        if (mcpConfigPath is not null)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        if (settings.StrictMcpConfig)
        {
            args.Add("--strict-mcp-config");
        }

        args.AddRange(settings.ExtraArgs);
        return args;
    }
}
