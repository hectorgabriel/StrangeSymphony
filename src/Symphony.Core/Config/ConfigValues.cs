using System.Text.RegularExpressions;

namespace Symphony.Core.Config;

public static partial class ConfigValues
{
    /// <summary>
    /// Resolves a value that is exactly <c>$NAME</c> or <c>${NAME}</c> from the environment.
    /// Returns the referenced env var name too. Empty resolutions count as missing (SPEC 5.3.1).
    /// </summary>
    public static (string? Value, string? EnvName) ResolveEnvReference(string? raw, Func<string, string?> env)
    {
        if (raw is null) return (null, null);
        var m = WholeEnvRef().Match(raw.Trim());
        if (!m.Success) return (raw, null);
        var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        var value = env(name);
        return (string.IsNullOrEmpty(value) ? null : value, name);
    }

    [GeneratedRegex(@"^(?:\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*))$")]
    private static partial Regex WholeEnvRef();
}
