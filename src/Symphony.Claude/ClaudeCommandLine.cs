using System.Diagnostics;
using System.Text;
using Symphony.Core.Agents;
using Symphony.Core.Config;
using Symphony.Core.Util;

namespace Symphony.Claude;

/// <summary>Builds the Claude Code headless invocation (stream-json in/out, one live process per worker).</summary>
public static class ClaudeCommandLine
{
    public const string TrackerMcpServerName = "tracker";

    public static IReadOnlyList<string> BuildArgs(ClaudeSettings settings, string sessionId, string? mcpConfigPath, bool hasTrackerTools, string title)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--session-id", sessionId,
            "--permission-mode", settings.PermissionMode,
            // Headless: anything that would prompt is denied instead of waiting (SPEC 10.5 "never stall").
            "--permission-prompts", "none",
            "--name", title,
        };

        if (settings.Model is { } model)
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

    /// <summary>
    /// SPEC 10.1: launch via <c>bash -lc &lt;command&gt;</c> in the workspace, with the shell chosen by
    /// <c>hooks.shell</c> (<see cref="ShellRunner.Resolve"/>). On Windows a command that names an <c>.exe</c>
    /// is started directly instead: a shell in between adds nothing there, and Windows PowerShell would
    /// re-encode the JSON stream in the console code page and mangle arguments that contain quotes.
    /// </summary>
    public static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args, string cwd, string shell = ShellRunner.Auto)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows() && FindWindowsExecutable(command, Environment.GetEnvironmentVariable) is { } exe)
        {
            psi = new ProcessStartInfo(exe);
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
        }
        else
        {
            var resolved = ShellRunner.Resolve(shell);
            var line = new StringBuilder(resolved.Kind == ShellKind.PowerShell ? "& " + command : command);
            foreach (var a in args)
            {
                line.Append(' ').Append(resolved.Quote(a));
            }

            if (OperatingSystem.IsWindows() && resolved.Kind == ShellKind.Bash && line.Length > ShellRunner.MaxGitBashCommandLine)
            {
                // Git Bash would drop it silently; stdin is the stream-json channel, so a script can't be piped.
                throw new AgentException(AgentErrorCodes.AgentNotFound,
                    $"the agent command line is {line.Length} characters, too long for Git Bash; set claude.command to the claude .exe so it starts directly");
            }

            psi = new ProcessStartInfo(resolved.Executable);
            foreach (var a in resolved.ScriptArgs(line.ToString()))
            {
                psi.ArgumentList.Add(a);
            }
        }

        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        return psi;
    }

    /// <summary>
    /// The <c>.exe</c> a Windows <c>claude.command</c> names, or null when it is anything else: a compound
    /// command, a <c>.cmd</c>/<c>.ps1</c> shim (npm installs), a script, or a name that isn't on PATH. Those go
    /// through the shell, which also turns "not found" into exit 127.
    /// </summary>
    public static string? FindWindowsExecutable(string command, Func<string, string?> env)
    {
        var trimmed = command.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        if (trimmed.Length == 0 || trimmed.IndexOfAny(['"', '\'', '&', '|', ';', '<', '>', '(', ')', '`', '$']) >= 0)
        {
            return null;
        }

        var ext = Path.GetExtension(trimmed);
        if (ext.Length > 0 && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = ext.Length > 0 ? trimmed : trimmed + ".exe";
        if (Path.IsPathFullyQualified(name) || name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            return null; // "claude --debug": a command line, not a program name
        }

        return (env("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), name))
            .FirstOrDefault(File.Exists);
    }
}
