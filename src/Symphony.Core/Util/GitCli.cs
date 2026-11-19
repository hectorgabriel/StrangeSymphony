using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Symphony.Core.Util;

/// <summary>Why a git call did not produce output: it could not start, ran too long, or exited non-zero.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Runs <c>git</c> in a workspace for host-side checks (pull requests, verification gates). Unattended:
/// a credential prompt fails instead of waiting.
/// </summary>
public static class GitCli
{
    public const int DefaultTimeoutMs = 120000;

    /// <summary>Standard output of <c>git <paramref name="args"/></c>; throws <see cref="GitException"/> on any failure.</summary>
    /// <remarks><c>symbolic-ref -q</c> exits 1 with no output on a detached HEAD; that returns <c>""</c>, the answer callers expect.</remarks>
    public static async Task<string> RunAsync(string workspacePath, CancellationToken ct, params string[] args) =>
        await RunAsync(workspacePath, DefaultTimeoutMs, ct, args);

    public static async Task<string> RunAsync(string workspacePath, int timeoutMs, CancellationToken ct, params string[] args)
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
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new GitException($"could not start git: {ex.Message}");
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ShellRunner.KillTree(process);
            ct.ThrowIfCancellationRequested();
            throw new GitException($"git {args[0]} did not finish within {timeoutMs / 1000} s");
        }

        var output = await stdout;
        var error = (await stderr).Trim();
        if (process.ExitCode != 0)
        {
            if (args is ["symbolic-ref", ..] && error.Length == 0) return "";
            throw new GitException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {(error.Length > 500 ? error[..500] : error)}");
        }

        return output;
    }

    public static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
