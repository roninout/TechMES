using System.Diagnostics;
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
/// При включённом HA работа дополнительно требует действующего общего lease.
/// </summary>
public sealed class RuntimeActivityState(IOptions<RuntimeOptions> options, ILogger<RuntimeActivityState> logger) : IPlantScadaWritePermission, IDisposable
{
    private readonly object _gate = new();
    private readonly string _nodeId = string.IsNullOrWhiteSpace(options.Value.NodeId) ? Environment.MachineName : options.Value.NodeId.Trim();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private RuntimeActivityMode _mode = RuntimeActivityMode.Starting;
    private string _reason = "Runtime is starting.";
    private DateTimeOffset _changedAtUtc = DateTimeOffset.UtcNow;
    private ActivitySession? _session;
    private readonly bool _coordinationRequired = options.Value.Redundancy.Enabled;
    private bool _standbyRequested = options.Value.Redundancy.Enabled || options.Value.StartInStandby;
    private int _pendingOperations;
    private TaskCompletionSource _idle = CreateIdleSignal();
    private Guid? _ownershipId;
    private long _ownershipDeadline;
    private Timer? _ownershipTimer;
    private bool _disposed;
    private RuntimeCoordinationStatusResponse _coordination = new() { Enabled = options.Value.Redundancy.Enabled, Role = options.Value.Redundancy.Enabled ? options.Value.Redundancy.Role : "Standalone", Message = options.Value.Redundancy.Enabled ? "Waiting for automatic coordination." : "Automatic coordination is disabled." };

    public bool CanWrite
    {
        get
        {
            lock (_gate)
                return IsActive();
        }
    }

    public string? WriteEpoch
    {
        get
        {
            lock (_gate)
                return IsActive() ? _session?.Id : null;
        }
    }

    /// <summary>
    /// Старый фоновый цикл не получает разрешение новой активации.
    /// </summary>
    public bool CanWriteInEpoch(string? epoch)
    {
        lock (_gate)
            return epoch is not null && IsActive() && _session?.Id == epoch;
    }

    /// <summary>
    /// Проверка эпохи и учёт записи выполняются под одной блокировкой.
    /// Draining дождётся завершения уже начатой нативной записи.
    /// </summary>
    public IDisposable? TryAcquireWrite(string? epoch)
    {
        lock (_gate)
            return epoch is not null && IsActive() && _session?.Id == epoch ? CreateLease(_session!) : null;
    }

    /// <summary>
    /// Читает только память процесса, без обращения к БД и SCADA.
    /// </summary>
    public RuntimeActivityStatusResponse GetStatus()
    {
        lock (_gate)
        {
            var canExecute = IsActive();

            return new RuntimeActivityStatusResponse { NodeId = _nodeId, InstanceId = _instanceId, Mode = _mode == RuntimeActivityMode.Active && !canExecute ? "Draining" : _mode.ToString(), CanExecuteCommands = canExecute, StandbyRequested = _standbyRequested, PendingOperations = _pendingOperations, ActivationId = _session?.Id, Reason = _reason, ChangedAtUtc = _changedAtUtc, Coordination = _coordination };
        }
    }

    /// <summary>
    /// Срок разрешения измеряется монотонными часами процесса.
    /// Изменение системного времени Windows не продлевает локальное право записи.
    /// </summary>
    public bool HasOwnership(Guid leaseId)
    {
        lock (_gate)
            return _ownershipId == leaseId && HasAuthority();
    }

    public void RenewOwnership(Guid leaseId, TimeSpan remaining)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (remaining <= TimeSpan.Zero || _ownershipId != leaseId && (_session is not null || _pendingOperations != 0))
                throw new InvalidOperationException("Previous Runtime operations must finish before accepting another ownership lease.");

            _ownershipId = leaseId;
            _ownershipDeadline = Stopwatch.GetTimestamp() + (long)(remaining.TotalSeconds * Stopwatch.Frequency);
            _ownershipTimer ??= new Timer(_ => ExpireOwnership(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ownershipTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }

    public void RevokeOwnership(string reason)
    {
        lock (_gate)
        {
            _ownershipId = null;
            _ownershipDeadline = 0;
            _ownershipTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        SetStandbyRequested(true);
        BeginDraining(reason);
    }

    public void SetCoordinationStatus(RuntimeCoordinationStatusResponse status)
    {
        lock (_gate)
            _coordination = status;
    }

    // Вызываются под _gate. Проверка срока выполняется также перед каждой новой операцией.
    private bool HasAuthority() => !_disposed && (!_coordinationRequired || _ownershipId is not null && Stopwatch.GetTimestamp() < _ownershipDeadline);

    private bool IsActive() => _mode == RuntimeActivityMode.Active && _session is not null && HasAuthority();

    private void ExpireOwnership()
    {
        ActivitySession? previous;

        lock (_gate)
        {
            if (_disposed || _ownershipId is null)
                return;

            var remaining = _ownershipDeadline - Stopwatch.GetTimestamp();

            if (remaining > 0)
            {
                _ownershipTimer?.Change(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), Timeout.InfiniteTimeSpan);
                return;
            }

            _ownershipId = null;
            _standbyRequested = true;
            previous = _session;
            _session = null;
            SetMode(RuntimeActivityMode.Draining, "Runtime ownership lease expired. Work is blocked.");
        }

        CancelSession(previous);
    }

    /// <summary>
    /// Задаёт желаемый режим. Отмена работы происходит сразу,
    /// а переход в Standby завершает worker после освобождения всех разрешений.
    /// </summary>
    public void SetStandbyRequested(bool requested)
    {
        ActivitySession? previous = null;

        lock (_gate)
        {
            if (_standbyRequested == requested)
                return;

            _standbyRequested = requested;

            if (requested)
            {
                previous = _session;
                _session = null;
                SetMode(RuntimeActivityMode.Draining, "Standby requested. Waiting for current operations to finish.");
            }
        }

        CancelSession(previous);
    }

    /// <summary>
    /// Отмена токена не доказывает завершение операции: ожидаем Dispose всех lease.
    /// </summary>
    public Task WaitForIdleAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return _idle.Task.WaitAsync(ct);
    }

    public RuntimeActivityLease BeginActivation() => TryBeginActivation() ?? throw new InvalidOperationException("Runtime activation is not permitted in the current state.");

    /// <summary>
    /// Подготовка требует действующего разрешения координатора.
    /// При штатной гонке с остановкой возвращает null.
    /// </summary>
    public RuntimeActivityLease? TryBeginActivation()
    {
        lock (_gate)
        {
            if (_standbyRequested || !HasAuthority() || _pendingOperations != 0 || _session is not null || _mode is RuntimeActivityMode.Active or RuntimeActivityMode.Draining)
                return null;

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
            if (_standbyRequested || !HasAuthority() || _mode != RuntimeActivityMode.Starting || _session?.Id != activation.ActivationId || activation.CancellationToken.IsCancellationRequested)
                return false;

            SetMode(RuntimeActivityMode.Active, "Runtime is ready for work.");

            return true;
        }
    }

    public RuntimeActivityLease Acquire()
    {
        lock (_gate)
        {
            if (!IsActive())
                throw new RuntimeNotActiveException();

            return CreateLease(_session!);
        }
    }

    public RuntimeActivityLease AcquireInitialization()
    {
        lock (_gate)
        {
            if (_mode != RuntimeActivityMode.Starting || _session is null || !HasAuthority())
                throw new RuntimeNotActiveException();

            return CreateLease(_session);
        }
    }

    /// <summary>
    /// Standby означает, что прежние операции действительно завершены.
    /// </summary>
    public void EnterStandby(string reason)
    {
        lock (_gate)
        {
            if (_session is not null || _pendingOperations != 0)
                throw new InvalidOperationException("Runtime cannot enter Standby while operations are still running.");

            SetMode(RuntimeActivityMode.Standby, reason);
        }
    }

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

        CancelSession(previous);
    }

    // Callbacks отмены нельзя выполнять под общей блокировкой состояния.
    private void CancelSession(ActivitySession? previous)
    {
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
        if (_mode == mode && _reason == reason)
            return;

        _mode = mode;
        _reason = reason;
        _changedAtUtc = DateTimeOffset.UtcNow;
    }

    private RuntimeActivityLease CreateLease(ActivitySession session)
    {
        session.AddReference();

        if (_pendingOperations++ == 0)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return new RuntimeActivityLease(session.Id, session.Token, () => ReleaseOperation(session));
    }

    private void ReleaseOperation(ActivitySession session)
    {
        session.Release();

        lock (_gate)
        {
            if (--_pendingOperations == 0)
                _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CreateIdleSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();

        return signal;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _ownershipTimer?.Dispose();
        }

        BeginDraining("Runtime is stopping.");
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