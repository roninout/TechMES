using Microsoft.Extensions.Options;
using TechMES.Application.Scada;
using TechMES.Contracts.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Runtime;

public enum RuntimeActivityMode
{
    Starting,
    Standby,
    Active,
    Draining,
    Faulted
}

/// <summary>
/// Один источник разрешения рабочих действий.
/// Выбор владельца между компьютерами будет добавлен следующим этапом.
/// </summary>
public sealed class RuntimeActivityState(IOptions<RuntimeOptions> options, ILogger<RuntimeActivityState> logger) : IPlantScadaWritePermission
{
    private readonly object _gate = new();
    private readonly string _nodeId = string.IsNullOrWhiteSpace(options.Value.NodeId) ? Environment.MachineName : options.Value.NodeId.Trim();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private RuntimeActivityMode _mode = RuntimeActivityMode.Starting;
    private string _reason = "Runtime is starting.";
    private DateTimeOffset _changedAtUtc = DateTimeOffset.UtcNow;
    private ActivitySession? _session;

    public bool CanWrite
    {
        get
        {
            lock (_gate)
                return _mode == RuntimeActivityMode.Active;
        }
    }

    public string? WriteEpoch
    {
        get
        {
            lock (_gate)
                return _mode == RuntimeActivityMode.Active ? _session?.Id : null;
        }
    }

    /// <summary>
    /// Старый фоновый цикл не получает разрешение новой активации.
    /// </summary>
    public bool CanWriteInEpoch(string? epoch)
    {
        lock (_gate)
            return epoch is not null && _mode == RuntimeActivityMode.Active && _session?.Id == epoch;
    }

    /// <summary>
    /// Читает только память процесса, без обращения к БД и SCADA.
    /// </summary>
    public RuntimeActivityStatusResponse GetStatus()
    {
        lock (_gate)
            return new RuntimeActivityStatusResponse { NodeId = _nodeId, InstanceId = _instanceId, Mode = _mode.ToString(), CanExecuteCommands = _mode == RuntimeActivityMode.Active, ActivationId = _session?.Id, Reason = _reason, ChangedAtUtc = _changedAtUtc };
    }

    /// <summary>
    /// Создаёт отдельную эпоху подготовки.
    /// Запоздавшая подготовка прошлой эпохи не сможет вернуть Runtime в Active.
    /// </summary>
    public RuntimeActivityLease BeginActivation()
    {
        lock (_gate)
        {
            if (_session is not null || _mode is RuntimeActivityMode.Active or RuntimeActivityMode.Draining)
                throw new InvalidOperationException("Runtime activation is already running or Runtime is draining.");

            _session = new ActivitySession();
            SetMode(RuntimeActivityMode.Starting, "Initializing Runtime dependencies.");

            return CreateLease(_session);
        }
    }

    /// <summary>
    /// Разрешает работу только после успешной подготовки текущей эпохи.
    /// </summary>
    public bool CompleteActivation(RuntimeActivityLease activation)
    {
        lock (_gate)
        {
            if (_mode != RuntimeActivityMode.Starting || _session?.Id != activation.ActivationId || activation.CancellationToken.IsCancellationRequested)
                return false;

            SetMode(RuntimeActivityMode.Active, "Runtime is ready for work.");

            return true;
        }
    }

    /// <summary>
    /// Выдаёт разрешение рабочей операции.
    /// При потере активности токен этой операции отменяется.
    /// </summary>
    public RuntimeActivityLease Acquire()
    {
        lock (_gate)
        {
            if (_mode != RuntimeActivityMode.Active || _session is null)
                throw new RuntimeNotActiveException();

            return CreateLease(_session);
        }
    }

    /// <summary>
    /// Отдельное разрешение для подготовки adapter-ов до перехода в Active.
    /// </summary>
    public RuntimeActivityLease AcquireInitialization()
    {
        lock (_gate)
        {
            if (_mode != RuntimeActivityMode.Starting || _session is null)
                throw new RuntimeNotActiveException();

            return CreateLease(_session);
        }
    }

    public void EnterStandby(string reason) => Deactivate(RuntimeActivityMode.Standby, reason);

    public void BeginDraining(string reason) => Deactivate(RuntimeActivityMode.Draining, reason);

    public void MarkFaulted(string reason) => Deactivate(RuntimeActivityMode.Faulted, reason);

    private void Deactivate(RuntimeActivityMode mode, string reason)
    {
        ActivitySession? previous;

        lock (_gate)
        {
            previous = _session;
            _session = null;

            SetMode(mode, reason);
        }

        // Callbacks отмены нельзя выполнять под общей блокировкой состояния.
        try
        {
            previous?.CancelAndReleaseOwner();
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex, "Runtime operation cancellation callback failed.");
        }
    }

    private void SetMode(RuntimeActivityMode mode, string reason)
    {
        _mode = mode;
        _reason = reason;
        _changedAtUtc = DateTimeOffset.UtcNow;
    }

    private static RuntimeActivityLease CreateLease(ActivitySession session)
    {
        session.AddReference();

        return new RuntimeActivityLease(session.Id, session.Token, session.Release);
    }

    private sealed class ActivitySession
    {
        private readonly CancellationTokenSource _stop = new();
        private int _references = 1;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public CancellationToken Token => _stop.Token;

        public void AddReference() => Interlocked.Increment(ref _references);

        public void CancelAndReleaseOwner()
        {
            try
            {
                _stop.Cancel();
            }
            finally
            {
                Release();
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                _stop.Dispose();
        }
    }
}

/// <summary>
/// Разрешение одной эпохи. Освобождение этого объекта не выключает Runtime.
/// </summary>
public sealed class RuntimeActivityLease(string activationId, CancellationToken cancellationToken, Action release) : IDisposable
{
    private Action? _release = release;

    public string ActivationId { get; } = activationId;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed class RuntimeNotActiveException : InvalidOperationException
{
    public RuntimeNotActiveException() : base("Runtime is not active.")
    {
    }
}