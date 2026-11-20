using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Symphony.Copilot;

/// <summary>
/// The <c>COPILOT_HOME</c> agent sessions run in. A run in a folder Copilot doesn't trust skips the repository's
/// hooks and <c>.mcp.json</c>, and <c>--trust-folder</c> can't be combined with <c>--session-id</c>, so trust comes
/// from <c>trustedFolders</c> in the home's <c>config.json</c>; a trusted folder's subfolders are trusted too.
/// </summary>
public static class CopilotHome
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Adds <paramref name="folder"/> (and its real path, when a symlink is on the way) to <c>trustedFolders</c>.</summary>
    public static async Task TrustAsync(string home, string folder, CancellationToken ct)
    {
        var wanted = new[] { Path.GetFullPath(folder), RealPath(folder) }.Distinct(StringComparer.Ordinal).ToList();
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, "config.json");
            var header = new List<string>();
            JsonObject config;
            if (File.Exists(path))
            {
                var lines = await File.ReadAllLinesAsync(path, ct);
                header.AddRange(lines.TakeWhile(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                var body = string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                try
                {
                    config = JsonNode.Parse(body.Length == 0 ? "{}" : body) as JsonObject
                        ?? throw new InvalidDataException("not a JSON object");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"{path} is not valid JSON, so Symphony can't add the workspace root to trustedFolders: {ex.Message}", ex);
                }
            }
            else
            {
                config = [];
            }

            var trusted = config["trustedFolders"] as JsonArray ?? [];
            var present = trusted.Select(n => n?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var missing = wanted.Where(w => !present.Contains(w)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            foreach (var m in missing)
            {
                trusted.Add(m);
            }

            config["trustedFolders"] = trusted.DeepClone();
            var text = new StringBuilder();
            foreach (var h in header)
            {
                text.Append(h).Append('\n');
            }

            text.Append(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).Append('\n');
            var tmp = path + ".symphony-tmp";
            await File.WriteAllTextAsync(tmp, text.ToString(), new UTF8Encoding(false), ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary><paramref name="path"/> with every symlinked directory on it resolved (macOS: /tmp is /private/tmp).</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }
}
