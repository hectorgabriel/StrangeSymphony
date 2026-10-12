using System.Diagnostics;
using System.Text;
using Symphony.Core.Config;

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

    /// <summary>SPEC 10.1: launch via <c>bash -lc &lt;command&gt;</c> in the workspace (PowerShell on Windows).</summary>
    public static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args, string cwd)
    {
        var line = new StringBuilder(command);
        foreach (var a in args)
        {
            line.Append(' ').Append(OperatingSystem.IsWindows() ? PowerShellQuote(a) : PosixQuote(a));
        }

        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("powershell.exe");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("& " + line);
        }
        else
        {
            psi = new ProcessStartInfo("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(line.ToString());
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

    public static string PosixQuote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ',' or '@')
            ? s
            : "'" + s.Replace("'", "'\\''") + "'";

    public static string PowerShellQuote(string s) => "'" + s.Replace("'", "''") + "'";
}
