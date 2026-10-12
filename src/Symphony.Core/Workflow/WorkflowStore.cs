using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Symphony.Core.Config;
using Symphony.Core.Templating;
using Symphony.Core.Tracker;

namespace Symphony.Core.Workflow;

/// <summary>An effective, validated workflow snapshot. Workers bind to one snapshot for their lifetime.</summary>
public sealed record EffectiveWorkflow(ServiceConfig Config, string PromptTemplate, ITrackerAdapter Tracker, int Version);

/// <summary>Keeps a snapshot's tracker adapter alive until disposed.</summary>
public sealed class WorkflowLease : IDisposable
{
    private WorkflowStore? _store;

    internal WorkflowLease(WorkflowStore store, EffectiveWorkflow workflow)
    {
        _store = store;
        Workflow = workflow;
    }

    public EffectiveWorkflow Workflow { get; }

    public void Dispose() => Interlocked.Exchange(ref _store, null)?.Release(Workflow);
}

/// <summary>
/// Owns WORKFLOW.md loading, file watching and hot reload (SPEC 6.2). Invalid reloads keep the
/// last known good snapshot and expose <see cref="LastError"/> so dispatch can be gated.
/// Snapshots are reference counted: the store holds one reference to the current snapshot and
/// callers take <see cref="WorkflowLease"/>s, so a replaced snapshot's tracker adapter is disposed
/// once nothing uses it.
/// </summary>
public sealed class WorkflowStore : IDisposable
{
    private readonly string _path;
    private readonly ConfigBuilder _builder;
    private readonly Func<string, string?> _env;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<EffectiveWorkflow, int> _refs = new(ReferenceEqualityComparer.Instance);
    private FileSystemWatcher? _watcher;
    private byte[]? _lastHash;
    private EffectiveWorkflow? _current;
    private int _version;
    private bool _disposed;

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
        ITrackerAdapter? retired;
        EffectiveWorkflow snapshot;
        lock (_gate)
        {
            (snapshot, var hash) = Build();
            retired = SetCurrentLocked(snapshot);
            _lastHash = hash;
            LogWarnings(snapshot);
        }

        retired?.Dispose();
        return snapshot;
    }

    /// <summary>Leases the current snapshot.</summary>
    public WorkflowLease Acquire()
    {
        lock (_gate)
        {
            return AcquireLocked(Current);
        }
    }

    /// <summary>Takes another lease on a snapshot the caller already holds a lease on.</summary>
    public WorkflowLease Acquire(EffectiveWorkflow workflow)
    {
        lock (_gate)
        {
            if (!_refs.ContainsKey(workflow))
            {
                throw new InvalidOperationException($"workflow snapshot {workflow.Version} was already released");
            }

            return AcquireLocked(workflow);
        }
    }

    internal void Release(EffectiveWorkflow workflow)
    {
        ITrackerAdapter? retired;
        lock (_gate)
        {
            retired = ReleaseLocked(workflow);
        }

        retired?.Dispose();
    }

    private WorkflowLease AcquireLocked(EffectiveWorkflow workflow)
    {
        _refs[workflow] = _refs.GetValueOrDefault(workflow) + 1;
        return new WorkflowLease(this, workflow);
    }

    /// <summary>Returns the adapter to dispose (outside the lock) when the last reference went away.</summary>
    private ITrackerAdapter? ReleaseLocked(EffectiveWorkflow workflow)
    {
        var count = _refs[workflow] - 1;
        if (count > 0)
        {
            _refs[workflow] = count;
            return null;
        }

        _refs.Remove(workflow);
        // A factory may hand the same adapter to several snapshots; keep it while any is alive.
        return _refs.Keys.Any(w => ReferenceEquals(w.Tracker, workflow.Tracker)) ? null : workflow.Tracker;
    }

    private ITrackerAdapter? SetCurrentLocked(EffectiveWorkflow snapshot)
    {
        var previous = _current;
        AcquireLocked(snapshot);
        _current = snapshot;
        return previous is null ? null : ReleaseLocked(previous);
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
        ITrackerAdapter? retired;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

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
                retired = SetCurrentLocked(snapshot);
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

        retired?.Dispose();
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

    /// <summary>Stops watching and drops the store's reference; outstanding leases keep their adapters.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        ITrackerAdapter? retired = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_current is not null)
            {
                retired = ReleaseLocked(_current);
            }
        }

        retired?.Dispose();
    }
}
