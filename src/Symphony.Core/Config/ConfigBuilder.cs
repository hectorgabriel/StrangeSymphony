using System.Globalization;
using System.Text.RegularExpressions;
using Symphony.Core.Domain;
using Symphony.Core.PullRequests;
using Symphony.Core.Tracker;
using Symphony.Core.Util;
using Symphony.Core.Workflow;

namespace Symphony.Core.Config;

public static class ConfigErrorCodes
{
    public const string InvalidConfig = "invalid_config";
    public const string MissingTrackerKind = "missing_tracker_kind";
}

/// <summary>
/// Resolves the typed view of WORKFLOW.md front matter (SPEC 6.1): defaults, <c>$VAR</c>
/// indirection for paths, coercion, and dispatch preflight validation (SPEC 6.3).
/// </summary>
public sealed partial class ConfigBuilder(
    IEnumerable<ITrackerAdapterFactory> factories,
    Func<string, string?> env,
    IEnumerable<IPullRequestProviderFactory>? pullRequestFactories = null)
{
    /// <summary>Directories and files a pull request may not contain unless <c>pull_request.forbidden_paths</c> says otherwise.</summary>
    public static readonly IReadOnlyList<string> DefaultForbiddenPaths = ["bin/", "obj/", "packages/", "node_modules/", ".vs/"];

    private readonly Dictionary<string, ITrackerAdapterFactory> _factories =
        factories.ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IPullRequestProviderFactory> _pullRequestFactories =
        (pullRequestFactories ?? []).ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> SupportedKinds => _factories.Keys;

    public ITrackerAdapterFactory FactoryFor(string kind) => _factories[kind];

    public ServiceConfig Build(WorkflowDefinition workflow, string workflowPath)
    {
        var root = workflow.Config;
        var warnings = new List<string>();
        var workflowDir = Path.GetDirectoryName(Path.GetFullPath(workflowPath)) ?? Directory.GetCurrentDirectory();

        if (root.ContainsKey("codex"))
        {
            warnings.Add("front matter key 'codex' is ignored by this implementation; use 'claude' instead");
        }

        // tracker
        var tracker = Section(root, "tracker");
        var kind = String(tracker, "tracker.kind", "kind");
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new WorkflowException(ConfigErrorCodes.MissingTrackerKind, "tracker.kind is required");
        }

        if (!_factories.TryGetValue(kind, out var factory))
        {
            throw new WorkflowException(TrackerErrorCategories.UnsupportedTrackerKind,
                $"tracker.kind '{kind}' is not supported (supported: {string.Join(", ", _factories.Keys)})");
        }

        var provider = tracker.TryGetValue("provider", out var p) && p is not null
            ? p as Dictionary<string, object?> ?? throw Invalid("tracker.provider", "must be a map")
            : new Dictionary<string, object?>();

        try
        {
            factory.ValidateProvider(provider, env);
        }
        catch (TrackerException ex)
        {
            throw new WorkflowException(ex.Category, ex.Message, ex);
        }

        var activeStates = StringList(tracker, "tracker.active_states", "active_states") ?? factory.StateDefaults.ActiveStates
            ?? throw Invalid("tracker.active_states", $"is required for tracker.kind '{kind}'");
        var terminalStates = StringList(tracker, "tracker.terminal_states", "terminal_states") ?? factory.StateDefaults.TerminalStates
            ?? throw Invalid("tracker.terminal_states", $"is required for tracker.kind '{kind}'");
        var requiredLabels = StringList(tracker, "tracker.required_labels", "required_labels") ?? [];

        // polling
        var polling = Section(root, "polling");
        var pollInterval = Int(polling, "polling.interval_ms", "interval_ms", 30000, min: 1);

        // workspace
        var workspace = Section(root, "workspace");
        var rootRaw = String(workspace, "workspace.root", "root");
        var workspaceRoot = rootRaw is null
            ? Path.Combine(Path.GetTempPath(), "symphony_workspaces")
            : ResolvePath(rootRaw, workflowDir, "workspace.root");

        // hooks
        var hooks = Section(root, "hooks");
        var shell = NullIfBlank(String(hooks, "hooks.shell", "shell")) ?? ShellRunner.Auto;
        if (!ShellRunner.IsValidSetting(shell))
        {
            throw Invalid("hooks.shell", "must be auto, bash, powershell, pwsh or an absolute path to a bash executable");
        }

        var hookSettings = new HookSettings(
            NullIfBlank(String(hooks, "hooks.after_create", "after_create")),
            NullIfBlank(String(hooks, "hooks.before_run", "before_run")),
            NullIfBlank(String(hooks, "hooks.after_run", "after_run")),
            NullIfBlank(String(hooks, "hooks.before_remove", "before_remove")),
            Int(hooks, "hooks.timeout_ms", "timeout_ms", 60000, min: 1),
            shell);

        // agent
        var agent = Section(root, "agent");
        var agentSettings = new AgentSettings(
            Int(agent, "agent.max_concurrent_agents", "max_concurrent_agents", 10, min: 1),
            Int(agent, "agent.max_turns", "max_turns", 20, min: 1),
            Int(agent, "agent.max_retry_backoff_ms", "max_retry_backoff_ms", 300000, min: 1),
            ByStateLimits(agent),
            Int(agent, "agent.max_fast_continuations", "max_fast_continuations", 3, min: 0));

        // claude
        var claude = Section(root, "claude");
        var command = String(claude, "claude.command", "command") ?? "claude";
        if (string.IsNullOrWhiteSpace(command))
        {
            throw Invalid("claude.command", "must be a non-empty shell command");
        }

        var claudeSettings = new ClaudeSettings(
            command,
            NullIfBlank(String(claude, "claude.model", "model")),
            ModelByState(claude, activeStates, warnings),
            String(claude, "claude.permission_mode", "permission_mode") ?? "acceptEdits",
            StringList(claude, "claude.allowed_tools", "allowed_tools") ?? [],
            StringList(claude, "claude.disallowed_tools", "disallowed_tools") ?? ["AskUserQuestion"],
            StringList(claude, "claude.extra_args", "extra_args") ?? [],
            Bool(claude, "claude.strict_mcp_config", "strict_mcp_config", true),
            Int(claude, "claude.turn_timeout_ms", "turn_timeout_ms", 3600000, min: 1),
            Int(claude, "claude.read_timeout_ms", "read_timeout_ms", 5000, min: 1),
            Int(claude, "claude.stall_timeout_ms", "stall_timeout_ms", 300000, min: int.MinValue));

        // server (SPEC 13.7 extension)
        var server = Section(root, "server");
        int? port = server.ContainsKey("port") ? Int(server, "server.port", "port", 0, min: 0) : null;

        var pullRequest = PullRequest(root, claudeSettings, warnings);

        return new ServiceConfig(
            Path.GetFullPath(workflowPath),
            new TrackerSettings(kind, provider, requiredLabels, activeStates, terminalStates),
            pollInterval,
            workspaceRoot,
            hookSettings,
            agentSettings,
            claudeSettings,
            port,
            warnings,
            pullRequest);
    }

    /// <summary><c>pull_request</c> (extension; docs/pull-requests.md). Absent means the agent gets no PR tool.</summary>
    private PullRequestSettings? PullRequest(IReadOnlyDictionary<string, object?> root, ClaudeSettings claude, List<string> warnings)
    {
        if (!root.TryGetValue("pull_request", out var raw) || raw is null)
        {
            return null;
        }

        var section = Section(root, "pull_request");
        var kind = NullIfBlank(String(section, "pull_request.kind", "kind"))
            ?? throw Invalid("pull_request.kind", $"is required (supported: {string.Join(", ", _pullRequestFactories.Keys)})");
        if (!_pullRequestFactories.TryGetValue(kind, out var factory))
        {
            throw Invalid("pull_request.kind", $"'{kind}' is not supported (supported: {string.Join(", ", _pullRequestFactories.Keys)})");
        }

        var provider = section.TryGetValue("provider", out var p) && p is not null
            ? p as Dictionary<string, object?> ?? throw Invalid("pull_request.provider", "must be a map")
            : new Dictionary<string, object?>();
        try
        {
            factory.ValidateProvider(provider, env);
        }
        catch (PullRequestException ex)
        {
            throw new WorkflowException(ex.Category, ex.Message, ex);
        }

        // The whole point of the tool is that nothing gets proposed unverified, so the command is mandatory.
        var verify = NullIfBlank(String(section, "pull_request.verify_command", "verify_command"))
            ?? throw Invalid("pull_request.verify_command", "is required: the build or test command that must pass before a pull request is opened");
        var target = NullIfBlank(String(section, "pull_request.target_branch", "target_branch"))
            ?? throw Invalid("pull_request.target_branch", "is required");

        var settings = new PullRequestSettings(
            kind,
            provider,
            NullIfBlank(String(section, "pull_request.remote", "remote")) ?? "origin",
            target.Trim(),
            Bool(section, "pull_request.draft", "draft", true),
            verify,
            Int(section, "pull_request.verify_timeout_ms", "verify_timeout_ms", 900000, min: 1000),
            Int(section, "pull_request.max_files", "max_files", 200, min: 1),
            StringList(section, "pull_request.forbidden_paths", "forbidden_paths") ?? [.. DefaultForbiddenPaths]);

        // A tool call produces no agent events, so a long build can look like a stalled session.
        if (claude.StallTimeoutMs > 0 && settings.VerifyTimeoutMs + PullRequestTool.OverheadMs >= claude.StallTimeoutMs)
        {
            warnings.Add($"pull_request.verify_timeout_ms ({settings.VerifyTimeoutMs}) plus {PullRequestTool.OverheadMs} ms for the checks reaches claude.stall_timeout_ms ({claude.StallTimeoutMs}); "
                + "a long verification can get the session stopped as stalled");
        }

        return settings;
    }

    /// <summary><c>~</c> expansion, <c>$VAR</c>/<c>${VAR}</c> expansion, relative-to-workflow, absolute normalization.</summary>
    public string ResolvePath(string raw, string baseDir, string field)
    {
        var expanded = EnvToken().Replace(raw, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var value = env(name);
            return string.IsNullOrEmpty(value)
                ? throw Invalid(field, $"references ${name}, which is unset or empty")
                : value;
        });

        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
        }

        if (string.IsNullOrWhiteSpace(expanded))
        {
            throw Invalid(field, "resolves to an empty path");
        }

        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDir, expanded));
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvToken();

    private static Dictionary<string, int> ByStateLimits(IReadOnlyDictionary<string, object?> agent)
    {
        var result = new Dictionary<string, int>();
        if (!agent.TryGetValue("max_concurrent_agents_by_state", out var raw) || raw is not Dictionary<string, object?> map)
        {
            return result;
        }

        foreach (var (state, value) in map)
        {
            // Invalid entries (non-positive or non-numeric) are ignored (SPEC 5.3.5).
            if (TryInt(value, out var n) && n > 0 && !string.IsNullOrWhiteSpace(state))
            {
                result[StateNames.Normalize(state)] = n;
            }
        }

        return result;
    }

    /// <summary><c>claude.model_by_state</c> (extension): a model per tracker state, keys normalized like state names.</summary>
    private static Dictionary<string, string> ModelByState(IReadOnlyDictionary<string, object?> claude, IReadOnlyList<string> activeStates, List<string> warnings)
    {
        var result = new Dictionary<string, string>();
        if (!claude.TryGetValue("model_by_state", out var raw) || raw is null)
        {
            return result;
        }

        if (raw is not Dictionary<string, object?> map)
        {
            throw Invalid("claude.model_by_state", "must be a map of state name to model");
        }

        foreach (var (state, value) in map)
        {
            // Unlike concurrency limits, a bad entry fails: silently falling back would run the stage on the wrong backend.
            var model = value switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                _ => throw Invalid($"claude.model_by_state.{state}", "must be a non-empty model name"),
            };

            var key = StateNames.Normalize(state);
            if (!activeStates.Any(a => StateNames.Normalize(a) == key))
            {
                warnings.Add($"claude.model_by_state names '{state}', which is not in tracker.active_states; no session starts in that state");
            }

            result[key] = model;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> Section(IReadOnlyDictionary<string, object?> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        return value as Dictionary<string, object?> ?? throw Invalid(key, "must be a map");
    }

    private static string? String(IReadOnlyDictionary<string, object?> map, string field, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s => s,
            long or double or bool => Convert.ToString(v, CultureInfo.InvariantCulture),
            _ => throw Invalid(field, "must be a string"),
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static List<string>? StringList(IReadOnlyDictionary<string, object?> map, string field, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return null;
        if (v is not List<object?> list) throw Invalid(field, "must be a list of strings");
        return list.Select(item => item switch
        {
            string s => s,
            long or double or bool => Convert.ToString(item, CultureInfo.InvariantCulture)!,
            null => string.Empty,
            _ => throw Invalid(field, "must be a list of strings"),
        }).ToList();
    }

    private static int Int(IReadOnlyDictionary<string, object?> map, string field, string key, int fallback, int min)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return fallback;
        if (!TryInt(v, out var n)) throw Invalid(field, "must be an integer");
        if (n < min) throw Invalid(field, $"must be >= {min}");
        return n;
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> map, string field, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => throw Invalid(field, "must be a boolean"),
        };
    }

    private static bool TryInt(object? v, out int n)
    {
        n = 0;
        switch (v)
        {
            case long l when l is >= int.MinValue and <= int.MaxValue:
                n = (int)l;
                return true;
            case string s when int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed):
                n = parsed;
                return true;
            default:
                return false;
        }
    }

    private static WorkflowException Invalid(string field, string message) =>
        new(ConfigErrorCodes.InvalidConfig, $"{field} {message}");
}
