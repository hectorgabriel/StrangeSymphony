using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Templating;
using Symphony.Core.Tracker;

namespace Symphony.Core.Workflow;

/// <summary>An effective, validated workflow snapshot. Workers bind to one snapshot for their lifetime.</summary>
public sealed record EffectiveWorkflow(ServiceConfig Config, string PromptTemplate, ITrackerAdapter Tracker, int Version);

/// <summary>
/// Owns WORKFLOW.md loading, file watching and hot reload (SPEC 6.2). Invalid reloads keep the
/// last known good snapshot and expose <see cref="LastError"/> so dispatch can be gated.
/// </summary>
public sealed class WorkflowStore : IDisposable
{
    private readonly string _path;
    private readonly ConfigBuilder _builder;
    private readonly Func<string, string?> _env;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private byte[]? _lastHash;
    private EffectiveWorkflow? _current;
    private int _version;

    public WorkflowStore(string path, ConfigBuilder builder, Func<string, string?> env, ILoggerFactory loggerFactory)
    {
        _path = Path.GetFullPath(path);
        _builder = builder;
        _env = env;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WorkflowStore>();
    }

    public string WorkflowPath => _path;

    public EffectiveWorkflow Current => _current ?? throw new InvalidOperationException("workflow not loaded");

    /// <summary>Non-null while the on-disk workflow is invalid; dispatch is blocked (SPEC 5.5).</summary>
    public WorkflowException? LastError { get; private set; }

    public event Action<EffectiveWorkflow>? Reloaded;

    /// <summary>Startup load. Throws <see cref="WorkflowException"/> so the CLI can fail startup.</summary>
    public EffectiveWorkflow LoadInitial()
    {
        lock (_gate)
        {
            var (snapshot, hash) = Build();
            _current = snapshot;
            _lastHash = hash;
            LogWarnings(snapshot);
            return snapshot;
        }
    }

    public void StartWatching()
    {
        var dir = Path.GetDirectoryName(_path)!;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ReloadIfChanged();
        _watcher.Created += (_, _) => ReloadIfChanged();
        _watcher.Renamed += (_, _) => ReloadIfChanged();
        _watcher.Deleted += (_, _) => ReloadIfChanged();
    }

    /// <summary>
    /// Re-reads the file when its content hash changed. Called from the watcher and defensively
    /// before each tick in case watch events are missed. Returns true when a new snapshot applied.
    /// </summary>
    public bool ReloadIfChanged()
    {
        lock (_gate)
        {
            byte[] hash;
            try
            {
                hash = SHA256.HashData(File.ReadAllBytes(_path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetError(new WorkflowException(WorkflowErrorCodes.MissingWorkflowFile, $"cannot read workflow file {_path}: {ex.Message}", ex));
                _lastHash = null;
                return false;
            }

            if (_lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash))
            {
                return false;
            }

            _lastHash = hash;
            try
            {
                var (snapshot, _) = Build();
                _current = snapshot;
                if (LastError is not null)
                {
                    _logger.LogInformation("action=workflow_reload outcome=recovered path={Path}", _path);
                }

                LastError = null;
                LogWarnings(snapshot);
                _logger.LogInformation("action=workflow_reload outcome=completed path={Path} version={Version}", _path, snapshot.Version);
            }
            catch (WorkflowException ex)
            {
                SetError(ex);
                return false;
            }
        }

        Reloaded?.Invoke(Current);
        return true;
    }

    private void SetError(WorkflowException ex)
    {
        LastError = ex;
        _logger.LogError("action=workflow_reload outcome=failed path={Path} code={Code} reason={Reason} note=keeping_last_known_good",
            _path, ex.Code, ex.Message);
    }

    private (EffectiveWorkflow, byte[]) Build()
    {
        var bytes = ReadBytes();
        var definition = WorkflowLoader.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var config = _builder.Build(definition, _path);
        // Surface template syntax errors at load time; rendering errors still fail per attempt.
        PromptRenderer.Parse(definition.PromptTemplate);
        var tracker = _builder.FactoryFor(config.Tracker.Kind).Create(config.Tracker, _env, _loggerFactory);
        return (new EffectiveWorkflow(config, definition.PromptTemplate, tracker, Interlocked.Increment(ref _version)), SHA256.HashData(bytes));
    }

    private byte[] ReadBytes()
    {
        try
        {
            return File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowException(WorkflowErrorCodes.MissingWorkflowFile, $"cannot read workflow file {_path}: {ex.Message}", ex);
        }
    }

    private void LogWarnings(EffectiveWorkflow snapshot)
    {
        foreach (var w in snapshot.Config.Warnings)
        {
            _logger.LogWarning("action=workflow_load outcome=warning reason={Reason}", w);
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
