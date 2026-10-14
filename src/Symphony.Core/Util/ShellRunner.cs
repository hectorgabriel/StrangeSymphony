using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Symphony.Core.Util;

public sealed record ShellResult(int? ExitCode, bool TimedOut, string Output)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public enum ShellKind
{
    Bash,
    PowerShell,
}

/// <summary>A concrete shell executable and the syntax it speaks.</summary>
public sealed record Shell(ShellKind Kind, string Executable)
{
    /// <summary>Arguments that make the shell run <paramref name="script"/> and exit.</summary>
    public IEnumerable<string> ScriptArgs(string script) => Kind == ShellKind.Bash
        ? ["-lc", script]
        : ["-NoProfile", "-NonInteractive", "-Command", script];

    /// <summary>Quotes one argument for this shell's command line.</summary>
    public string Quote(string s) => Kind == ShellKind.Bash ? PosixQuote(s) : PowerShellQuote(s);

    public static string PosixQuote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ',' or '@')
            ? s
            : "'" + s.Replace("'", "'\\''") + "'";

    public static string PowerShellQuote(string s) => "'" + s.Replace("'", "''") + "'";
}

/// <summary>
/// Runs trusted shell scripts (hooks) with a timeout. The shell comes from <c>hooks.shell</c>: <c>auto</c> is
/// <c>bash -lc</c> (SPEC 9.4); on Windows that bash is Git Bash, never WSL. See <see cref="Resolve"/>.
/// </summary>
public static class ShellRunner
{
    public const int MaxCapturedChars = 8192;
    public const string Auto = "auto";

    private static readonly ConcurrentDictionary<string, Shell> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="value"/> is something <see cref="Resolve"/> accepts.</summary>
    public static bool IsValidSetting(string value) =>
        value.ToLowerInvariant() is Auto or "bash" or "powershell" or "pwsh" || Path.IsPathFullyQualified(value);

    /// <summary>
    /// Turns a <c>hooks.shell</c> value into an executable, once per value. <c>auto</c> and <c>bash</c> mean
    /// <c>bash</c> on POSIX. On Windows they mean Git Bash, found by path: a bare <c>bash</c> there usually
    /// resolves to <c>System32\bash.exe</c>, the WSL launcher, which runs scripts in another OS.
    /// <c>auto</c> falls back to Windows PowerShell when Git Bash is missing; <c>bash</c> does not.
    /// </summary>
    public static Shell Resolve(string setting, Func<string, string?>? env = null) =>
        env is null ? Resolved.GetOrAdd(setting, s => ResolveUncached(s, Environment.GetEnvironmentVariable)) : ResolveUncached(setting, env);

    private static Shell ResolveUncached(string setting, Func<string, string?> env)
    {
        switch (setting.ToLowerInvariant())
        {
            case "powershell":
                return new Shell(ShellKind.PowerShell, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
            case "pwsh":
                return new Shell(ShellKind.PowerShell, "pwsh");
            case Auto or "bash":
                if (!OperatingSystem.IsWindows())
                {
                    return new Shell(ShellKind.Bash, "bash");
                }

                if (FindGitBash(env) is { } gitBash)
                {
                    return new Shell(ShellKind.Bash, gitBash);
                }

                return setting.Equals(Auto, StringComparison.OrdinalIgnoreCase)
                    ? new Shell(ShellKind.PowerShell, "powershell.exe")
                    : throw new FileNotFoundException(
                        "Git Bash was not found; install Git for Windows or set CLAUDE_CODE_GIT_BASH_PATH to its bash.exe");
            default:
                // An explicit path is taken as given: the user chose it.
                return new Shell(ShellKind.Bash, setting);
        }
    }

    /// <summary>
    /// Git Bash on Windows, in the order Claude Code itself uses: <c>CLAUDE_CODE_GIT_BASH_PATH</c>, then the
    /// installation that owns <c>git.exe</c> on PATH, then the default install folder. Anything under the
    /// Windows directory is skipped, because that is the WSL launcher.
    /// <para>
    /// Within an installation the real shell, <c>usr\bin\bash.exe</c>, wins over <c>bin\bash.exe</c>: the
    /// latter is a redirector that re-launches the former and cuts the command line at about 8 KB, so a
    /// long hook would arrive truncated without any error.
    /// </para>
    /// </summary>
    public static string? FindGitBash(Func<string, string?> env)
    {
        var candidates = new List<string>();
        if (env("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured)
        {
            if (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(configured))) is { } configuredRoot
                && Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(configured))) is { } binDir
                && binDir.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(configuredRoot, "usr", "bin", "bash.exe"));
            }

            candidates.Add(configured);
        }

        foreach (var dir in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(Path.Combine(dir, "git.exe")))
            {
                continue;
            }

            // git.exe lives in <root>\cmd or <root>\bin (or <root>\mingw64\bin); bash.exe in <root>\bin.
            for (var root = Path.GetDirectoryName(Path.GetFullPath(dir)); root is not null; root = Path.GetDirectoryName(root))
            {
                candidates.Add(Path.Combine(root, "usr", "bin", "bash.exe"));
                candidates.Add(Path.Combine(root, "bin", "bash.exe"));
                if (File.Exists(Path.Combine(root, "git-bash.exe")))
                {
                    break;
                }
            }
        }

        foreach (var programFiles in new[] { env("ProgramFiles"), env("ProgramW6432"), env("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null })
        {
            if (!string.IsNullOrEmpty(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Git", "usr", "bin", "bash.exe"));
                candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
            }
        }

        var windows = env("SystemRoot") ?? env("windir");
        return candidates.FirstOrDefault(c =>
            File.Exists(c)
            && !(windows is { Length: > 0 } && Path.GetFullPath(c).StartsWith(Path.TrimEndingDirectorySeparator(windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Longest command line Git Bash takes from a Windows parent. Past roughly 8 KB the MSYS runtime drops the
    /// arguments and bash exits 0 having run nothing, so anything longer must not go through <c>-c</c>.
    /// </summary>
    public const int MaxGitBashCommandLine = 7000;

    /// <summary>
    /// The process for one hook. On Windows, bash reads the script from a temporary file (returned so the
    /// caller deletes it) because of <see cref="MaxGitBashCommandLine"/>; stdin is not an option, since any
    /// command in the hook that reads stdin would swallow the rest of the script.
    /// </summary>
    public static (ProcessStartInfo StartInfo, string? ScriptFile) ShellStartInfo(string script, string cwd, string shell = Auto)
    {
        var resolved = Resolve(shell);
        var psi = new ProcessStartInfo(resolved.Executable);
        string? scriptFile = null;
        if (OperatingSystem.IsWindows() && resolved.Kind == ShellKind.Bash)
        {
            scriptFile = Path.Combine(Path.GetTempPath(), $"symphony-hook-{Guid.NewGuid():N}.sh");
            File.WriteAllText(scriptFile, script.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(scriptFile.Replace('\\', '/'));
        }
        else
        {
            foreach (var arg in resolved.ScriptArgs(script))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        return (psi, scriptFile);
    }

    public static async Task<ShellResult> RunAsync(string script, string cwd, int timeoutMs, CancellationToken ct, string shell = Auto)
    {
        var (psi, scriptFile) = ShellStartInfo(script, cwd, shell);
        try
        {
            return await RunAsync(psi, timeoutMs, ct);
        }
        finally
        {
            if (scriptFile is not null)
            {
                try
                {
                    File.Delete(scriptFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A temp file left behind is harmless.
                }
            }
        }
    }

    private static async Task<ShellResult> RunAsync(ProcessStartInfo psi, int timeoutMs, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        // Otherwise Windows decodes in the OEM code page and mangles accents in hook output.
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var truncated = false;
        void Append(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                // Cap while capturing, so one huge line never lands in memory whole.
                var room = MaxCapturedChars - output.Length;
                if (line.Length > room)
                {
                    output.Append(line, 0, Math.Max(room, 0));
                    truncated = true;
                }
                else
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            ct.ThrowIfCancellationRequested();
            return new ShellResult(null, true, Captured(output, ref truncated));
        }

        // Flush async readers.
        process.WaitForExit();
        return new ShellResult(process.ExitCode, false, Captured(output, ref truncated));
    }

    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Captured output, marked whenever anything was dropped.</summary>
    private static string Captured(StringBuilder sb, ref bool truncated)
    {
        lock (sb)
        {
            var s = sb.ToString().TrimEnd();
            return truncated ? s + "…[truncated]" : s;
        }
    }
}
