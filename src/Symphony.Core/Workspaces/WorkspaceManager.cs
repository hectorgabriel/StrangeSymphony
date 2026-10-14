using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Util;

namespace Symphony.Core.Workspaces;

public sealed record Workspace(string Path, string WorkspaceKey, bool CreatedNow);

public sealed class WorkspaceException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class WorkspaceKeys
{
    /// <summary>
    /// SPEC 4.2 / 9.5: replace chars outside <c>[A-Za-z0-9._-]</c> with <c>_</c>; when that changes the
    /// identifier, append <c>-</c> + 16 hex chars (64 bits) of SHA-256 over the original identifier.
    /// </summary>
    public static string For(string identifier)
    {
        var sb = new StringBuilder(identifier.Length);
        foreach (var c in identifier)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        var sanitized = sb.ToString();
        // "." and ".." are valid characters but would escape or alias the root. Windows also refuses device
        // names (CON, NUL, COM1…, with or without an extension) and drops a trailing dot, so "WI-1." and
        // "WI-1" would share a folder. Hashing those keeps the key the same on every OS.
        if (sanitized == identifier && sanitized is not ("" or "." or "..") && !sanitized.EndsWith('.') && !IsWindowsDeviceName(sanitized))
        {
            return sanitized;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return $"{sanitized}-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}";
    }

    private static bool IsWindowsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9');
    }
}

/// <summary>Per-issue workspace lifecycle and hooks (SPEC 9).</summary>
public sealed class WorkspaceManager(ILogger<WorkspaceManager> logger)
{
    public static string PathFor(string workspaceRoot, string identifier)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var path = Path.GetFullPath(Path.Combine(root, WorkspaceKeys.For(identifier)));
        EnsureInsideRoot(root, path);
        return path;
    }

    /// <summary>SPEC 9.5 invariant 2: workspace path must have the root as a prefix directory.</summary>
    public static void EnsureInsideRoot(string workspaceRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) || full.Length <= root.Length + 1)
        {
            throw new WorkspaceException("invalid_workspace_cwd", $"workspace path {full} is outside workspace root {root}");
        }
    }

    public async Task<Workspace> CreateForIssueAsync(string identifier, ServiceConfig config, CancellationToken ct)
    {
        var key = WorkspaceKeys.For(identifier);
        var path = PathFor(config.WorkspaceRoot, identifier);

        Directory.CreateDirectory(config.WorkspaceRoot);
        if (File.Exists(path))
        {
            // Policy: fail rather than delete an unexpected file (SPEC 17.2).
            throw new WorkspaceException("workspace_path_not_directory", $"workspace path {path} exists and is not a directory");
        }

        var createdNow = false;
        if (Directory.Exists(path))
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                throw new WorkspaceException("invalid_workspace_cwd", $"workspace path {path} is a symbolic link");
            }
        }
        else
        {
            Directory.CreateDirectory(path);
            createdNow = true;
        }

        if (createdNow && config.Hooks.AfterCreate is { } afterCreate)
        {
            var ok = await RunHookAsync("after_create", afterCreate, path, identifier, config.Hooks, ct);
            if (!ok)
            {
                TryDelete(path);
                throw new WorkspaceException("hook_failed", "after_create hook failed");
            }
        }

        return new Workspace(path, key, createdNow);
    }

    public async Task<bool> RunHookAsync(string name, string script, string cwd, string identifier, HookSettings hooks, CancellationToken ct)
    {
        var timeoutMs = hooks.TimeoutMs;
        logger.LogInformation("action=hook hook={Hook} outcome=started issue_identifier={IssueIdentifier}", name, identifier);
        ShellResult result;
        try
        {
            WarnOnShellFallback(hooks.Shell);
            result = await ShellRunner.RunAsync(script, cwd, timeoutMs, ct, hooks.Shell);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("action=hook hook={Hook} outcome=failed issue_identifier={IssueIdentifier} reason={Reason}", name, identifier, ex.Message);
            return false;
        }

        if (result.TimedOut)
        {
            logger.LogWarning("action=hook hook={Hook} outcome=timed_out issue_identifier={IssueIdentifier} timeout_ms={TimeoutMs} output={Output}",
                name, identifier, timeoutMs, result.Output);
            return false;
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning("action=hook hook={Hook} outcome=failed issue_identifier={IssueIdentifier} exit_code={ExitCode} output={Output}",
                name, identifier, result.ExitCode, result.Output);
            return false;
        }

        logger.LogInformation("action=hook hook={Hook} outcome=completed issue_identifier={IssueIdentifier}", name, identifier);
        return true;
    }

    /// <summary>Runs <c>before_remove</c> (failures ignored) then deletes the workspace (SPEC 9.4).</summary>
    public async Task RemoveAsync(string identifier, ServiceConfig config, CancellationToken ct)
    {
        string path;
        try
        {
            path = PathFor(config.WorkspaceRoot, identifier);
        }
        catch (WorkspaceException ex)
        {
            logger.LogWarning("action=workspace_cleanup outcome=failed issue_identifier={IssueIdentifier} reason={Reason}", identifier, ex.Message);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        if (config.Hooks.BeforeRemove is { } beforeRemove)
        {
            await RunHookAsync("before_remove", beforeRemove, path, identifier, config.Hooks, ct);
        }

        if (TryDelete(path))
        {
            logger.LogInformation("action=workspace_cleanup outcome=completed issue_identifier={IssueIdentifier} path={Path}", identifier, path);
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            DeleteDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("action=workspace_cleanup outcome=failed path={Path} reason={Reason}", path, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Deletes a workspace tree. A link is removed without following it. On Windows, git writes its objects
    /// read-only, which <see cref="Directory.Delete(string, bool)"/> refuses, so the attribute is cleared first;
    /// and a just-killed agent can hold a file for a moment, so a failed delete is retried briefly.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
        {
            Directory.Delete(path); // remove the link only
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    ClearReadOnly(info);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5 && OperatingSystem.IsWindows())
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo root)
    {
        // Don't descend through links: their targets are not ours to touch.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var entry in root.EnumerateFileSystemInfos("*", options))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    private int _fallbackWarned;

    private void WarnOnShellFallback(string setting)
    {
        if (OperatingSystem.IsWindows()
            && setting.Equals(ShellRunner.Auto, StringComparison.OrdinalIgnoreCase)
            && ShellRunner.Resolve(setting).Kind == ShellKind.PowerShell
            && Interlocked.Exchange(ref _fallbackWarned, 1) == 0)
        {
            logger.LogWarning("action=hook outcome=shell_fallback shell=powershell.exe reason={Reason}",
                "Git Bash not found; hooks run in Windows PowerShell. Install Git for Windows or set hooks.shell.");
        }
    }
}
