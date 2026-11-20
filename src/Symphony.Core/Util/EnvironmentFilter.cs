namespace Symphony.Core.Util;

/// <summary>
/// Removes variables from a child process's environment by name pattern. A pattern is a variable name in
/// which <c>*</c> matches any run of characters (<c>ConnectionStrings__*</c>, <c>*_PASSWORD</c>). Matching
/// ignores case on every platform: stripping <c>Db_Password</c> along with <c>DB_PASSWORD</c> errs on the safe side.
/// </summary>
public static class EnvironmentFilter
{
    /// <summary>
    /// Names that usually hold a database connection or its credentials. Symphony warns at load when one is in its
    /// environment and not stripped, because every process it starts in a workspace would inherit it.
    /// </summary>
    public static readonly IReadOnlyList<string> DatabaseCredentialPatterns =
    [
        "ConnectionStrings__*", "*CONNECTION_STRING*", "*CONNECTIONSTRING*", "*DATABASE_URL*", "DB_*", "*_DB_*",
        "PGPASSWORD", "PGPASSFILE", "PGSERVICE", "PGHOST", "PGUSER", "MYSQL_*", "SQLCMD*", "MONGO*", "*REDIS_URL*",
    ];

    /// <summary>Removes every variable that matches one of <paramref name="patterns"/>; returns the removed names.</summary>
    public static IReadOnlyList<string> Remove(IDictionary<string, string?> environment, IEnumerable<string> patterns)
    {
        var list = patterns as IReadOnlyCollection<string> ?? patterns.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var removed = environment.Keys.Where(name => MatchesAny(name, list)).ToList();
        foreach (var name in removed)
        {
            environment.Remove(name);
        }

        return removed;
    }

    public static bool MatchesAny(string name, IEnumerable<string> patterns) => patterns.Any(p => Matches(name, p));

    public static bool Matches(string name, string pattern)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        // Anchored first and last parts, the middle ones in order: enough for a glob with only '*'.
        var span = name.AsSpan();
        if (!span.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[parts[0].Length..];
        var last = parts[^1];
        if (span.Length < last.Length || !span.EndsWith(last, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[..^last.Length];
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var at = span.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            span = span[(at + parts[i].Length)..];
        }

        return true;
    }

    /// <summary>
    /// The names in <paramref name="names"/> that look like database credentials and that
    /// <paramref name="stripped"/> does not remove, sorted.
    /// </summary>
    public static IReadOnlyList<string> UnstrippedDatabaseCredentials(IEnumerable<string> names, IEnumerable<string> stripped)
    {
        var strip = stripped.ToList();
        return names
            .Where(n => MatchesAny(n, DatabaseCredentialPatterns) && !MatchesAny(n, strip))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
