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
        var stripEnv = StripEnv(workspace);

        // audit (extension; docs/audit-trail.md)
        var audit = Section(root, "audit");
        var auditRaw = NullIfBlank(String(audit, "audit.path", "path"));
        var auditPath = auditRaw is null ? null : ResolvePath(auditRaw, workflowDir, "audit.path");

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
        var runner = Agents.AgentRunners.Normalize(String(agent, "agent.runner", "runner"));
        if (!Agents.AgentRunners.All.Contains(runner))
        {
            throw Invalid("agent.runner", $"must be one of: {string.Join(", ", Agents.AgentRunners.All)}");
        }

        var agentSettings = new AgentSettings(
            Int(agent, "agent.max_concurrent_agents", "max_concurrent_agents", 10, min: 1),
            Int(agent, "agent.max_turns", "max_turns", 20, min: 1),
            Int(agent, "agent.max_retry_backoff_ms", "max_retry_backoff_ms", 300000, min: 1),
            ByStateLimits(agent),
            Int(agent, "agent.max_fast_continuations", "max_fast_continuations", 3, min: 0),
            runner);

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
            Int(claude, "claude.stall_timeout_ms", "stall_timeout_ms", 300000, min: int.MinValue),
            Bool(claude, "claude.resume", "resume", true));

        // budget (extension)
        var budget = Section(root, "budget");
        var budgetSettings = new BudgetSettings(
            Long(budget, "budget.max_tokens_per_issue", "max_tokens_per_issue"),
            Long(budget, "budget.max_tokens_per_day", "max_tokens_per_day"));

        // server (SPEC 13.7 extension)
        var server = Section(root, "server");
        int? port = server.ContainsKey("port") ? Int(server, "server.port", "port", 0, min: 0) : null;

        // Gates and the PR tool run inside a tool call, which the stall timer can't see; warn against the runner's timeout.
        var stall = new StallLimit(claudeSettings.StallTimeoutMs, $"{runner}.stall_timeout_ms");
        var pullRequest = PullRequest(root, stall, warnings);
        var gates = Gates(root, stall, warnings);
        var defects = Defects(root);

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
            pullRequest,
            budgetSettings,
            gates,
            stripEnv,
            auditPath,
            defects);
    }

    /// <summary><c>defects</c> (extension; docs/rework-loops.md). Absent means no <c>report_defects</c> tool.</summary>
    private static DefectSettings? Defects(IReadOnlyDictionary<string, object?> root)
    {
        if (!root.TryGetValue("defects", out var raw) || raw is null)
        {
            return null;
        }

        if (raw is not Dictionary<string, object?> map)
        {
            throw Invalid("defects", "must be a map with 'states' and 'categories'");
        }

        var unknown = map.Keys.Except(["states", "categories"], StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw Invalid("defects", $"has unknown key(s) {string.Join(", ", unknown)} (allowed: states, categories)");
        }

        var states = (map.TryGetValue("states", out var s) && s is string one ? [one] : StringList(map, "defects.states", "states") ?? [])
            .Select(x => x.Trim()).ToList();
        if (states.Count == 0 || states.Any(x => x.Length == 0))
        {
            throw Invalid("defects.states", "must name the status (or statuses) that send work back, such as \"Con Defectos\"");
        }

        // A list of names, or a map of name to description.
        var categories = new List<KeyValuePair<string, string>>();
        switch (map.GetValueOrDefault("categories"))
        {
            case List<object?>:
                categories.AddRange(StringList(map, "defects.categories", "categories")!.Select(c => KeyValuePair.Create(c.Trim(), "")));
                break;
            case Dictionary<string, object?> named:
                foreach (var (name, description) in named)
                {
                    categories.Add(KeyValuePair.Create(name.Trim(), description switch
                    {
                        null => "",
                        string d => d.Trim(),
                        _ => throw Invalid($"defects.categories.{name}", "must be a description"),
                    }));
                }

                break;
            default:
                throw Invalid("defects.categories", "must be a list of category names, or a map of name to description");
        }

        if (categories.Count == 0)
        {
            throw Invalid("defects.categories", "must have at least one category");
        }

        foreach (var (name, _) in categories)
        {
            // Names end up as metric keys and enum values in the tool's schema: keep them plain.
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]{0,39}$"))
            {
                throw Invalid("defects.categories", $"'{name}' must be lowercase letters, digits and '_', starting with a letter, at most 40 characters");
            }
        }

        if (categories.Select(c => c.Key).Distinct().Count() != categories.Count)
        {
            throw Invalid("defects.categories", "has a category more than once");
        }

        return new DefectSettings(states.Select(StateNames.Normalize).Distinct().ToList(), categories);
    }

    /// <summary><c>workspace.strip_env</c> (extension; docs/data-isolation.md): names or <c>*</c> patterns.</summary>
    private static List<string> StripEnv(IReadOnlyDictionary<string, object?> workspace)
    {
        const string field = "workspace.strip_env";
        var list = workspace.TryGetValue("strip_env", out var raw) && raw is string one
            ? [one]
            : StringList(workspace, field, "strip_env") ?? [];
        foreach (var pattern in list)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern != pattern.Trim() || pattern.Contains('='))
            {
                throw Invalid(field, $"'{pattern}' is not a variable name or pattern");
            }

            // A pattern that takes PATH or HOME with it leaves the agent, hooks and builds unable to start.
            if (Util.EnvironmentFilter.Matches("PATH", pattern) || Util.EnvironmentFilter.Matches("HOME", pattern))
            {
                throw Invalid(field, $"'{pattern}' would also strip PATH or HOME; name the variables more narrowly");
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Time a gate may use on top of its commands' <c>timeout_ms</c>, for the git checks.</summary>
    public const int GateOverheadMs = 120000;

    /// <summary>The runner's stall timeout and the front matter field that sets it, for warnings.</summary>
    private sealed record StallLimit(int Ms, string Field);

    /// <summary><c>gates</c> (extension; docs/verification-gates.md): target state name -> host-side checks.</summary>
    private static Dictionary<string, GateSettings> Gates(
        IReadOnlyDictionary<string, object?> root, StallLimit stall, List<string> warnings)
    {
        var result = new Dictionary<string, GateSettings>();
        if (!root.TryGetValue("gates", out var raw) || raw is null)
        {
            return result;
        }

        if (raw is not Dictionary<string, object?> map)
        {
            throw Invalid("gates", "must be a map of target state name to gate");
        }

        foreach (var (state, value) in map)
        {
            var field = $"gates.{state}";
            if (string.IsNullOrWhiteSpace(state))
            {
                throw Invalid("gates", "has an empty state name");
            }

            // A misspelt gate would let the agent through unchecked, so every entry must be well formed.
            if (value is not Dictionary<string, object?> gate)
            {
                throw Invalid(field, "must be a map with 'run' and/or 'require_pushed'");
            }

            var unknown = gate.Keys.Except(["run", "require_pushed", "remote", "timeout_ms"], StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw Invalid(field, $"has unknown key(s) {string.Join(", ", unknown)} (allowed: run, require_pushed, remote, timeout_ms)");
            }

            List<string> run = gate.TryGetValue("run", out var r) ? r switch
            {
                null => [],
                string s => [s],
                List<object?> => StringList(gate, $"{field}.run", "run")!,
                _ => throw Invalid($"{field}.run", "must be a command or a list of commands"),
            } : [];
            if (run.Any(string.IsNullOrWhiteSpace))
            {
                throw Invalid($"{field}.run", "must not contain empty commands");
            }

            var settings = new GateSettings(
                state.Trim(),
                run,
                Bool(gate, $"{field}.require_pushed", "require_pushed", true),
                NullIfBlank(String(gate, $"{field}.remote", "remote"))?.Trim() ?? "origin",
                Int(gate, $"{field}.timeout_ms", "timeout_ms", 900000, min: 1000));
            if (settings.Run.Count == 0 && !settings.RequirePushed)
            {
                throw Invalid(field, "checks nothing: give it 'run' commands or leave 'require_pushed' on");
            }

            if (stall.Ms > 0 && settings.Run.Count > 0 && settings.TimeoutMs + GateOverheadMs >= stall.Ms)
            {
                warnings.Add($"{field}.timeout_ms ({settings.TimeoutMs}) plus {GateOverheadMs} ms for the git checks reaches {stall.Field} ({stall.Ms}); "
                    + "a long gate can get the session stopped as stalled");
            }

            result[StateNames.Normalize(state)] = settings;
        }

        return result;
    }

    /// <summary><c>pull_request</c> (extension; docs/pull-requests.md). Absent means the agent gets no PR tool.</summary>
    private PullRequestSettings? PullRequest(IReadOnlyDictionary<string, object?> root, StallLimit stall, List<string> warnings)
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
        if (stall.Ms > 0 && settings.VerifyTimeoutMs + PullRequestTool.OverheadMs >= stall.Ms)
        {
            warnings.Add($"pull_request.verify_timeout_ms ({settings.VerifyTimeoutMs}) plus {PullRequestTool.OverheadMs} ms for the checks reaches {stall.Field} ({stall.Ms}); "
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

    /// <summary>A non-negative count; absent means 0. Underscores are allowed as digit separators (<c>5_000_000</c>).</summary>
    private static long Long(IReadOnlyDictionary<string, object?> map, string field, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return 0;
        var n = v switch
        {
            long l => l,
            string s when long.TryParse(s.Trim().Replace("_", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw Invalid(field, "must be a whole number of tokens"),
        };
        return n >= 0 ? n : throw Invalid(field, "must be >= 0");
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
