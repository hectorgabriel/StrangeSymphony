using System.Diagnostics;
using System.Text;

namespace Symphony.Core.Util;

public sealed record ShellResult(int? ExitCode, bool TimedOut, string Output)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs trusted shell scripts (hooks) with a timeout. POSIX: <c>bash -lc</c>; Windows: PowerShell.</summary>
public static class ShellRunner
{
    public const int MaxCapturedChars = 8192;

    public static ProcessStartInfo ShellStartInfo(string script, string cwd)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("powershell.exe");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
        }
        else
        {
            psi = new ProcessStartInfo("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(script);
        }

        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        return psi;
    }

    public static async Task<ShellResult> RunAsync(string script, string cwd, int timeoutMs, CancellationToken ct)
    {
        var psi = ShellStartInfo(script, cwd);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                if (output.Length < MaxCapturedChars)
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
            return new ShellResult(null, true, Truncate(output));
        }

        // Flush async readers.
        process.WaitForExit();
        return new ShellResult(process.ExitCode, false, Truncate(output));
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

    private static string Truncate(StringBuilder sb)
    {
        lock (sb)
        {
            var s = sb.ToString().TrimEnd();
            return s.Length > MaxCapturedChars ? s[..MaxCapturedChars] + "…[truncated]" : s;
        }
    }
}
