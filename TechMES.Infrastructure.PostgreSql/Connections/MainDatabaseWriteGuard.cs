using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Runtime;

namespace TechMES.Infrastructure.PostgreSql.Connections;

/// <summary>Блокирует новые операции после обнаружения двух Primary, разных кластеров или повторного endpoint.</summary>
public sealed class MainDatabaseWriteGuard
{
    private readonly object _gate = new();
    private readonly ILogger<MainDatabaseWriteGuard> _logger;
    private readonly int _recoveryThreshold;
    private MainDatabaseWriteSafety _state;
    private DateTimeOffset? _lastPrimaryCheck;
    private DateTimeOffset? _lastSecondaryCheck;

    /// <summary>Использует существующий RecoveryThreshold; отдельный таймер и новые настройки не нужны.</summary>
    public MainDatabaseWriteGuard(IConfiguration configuration, ILogger<MainDatabaseWriteGuard> logger)
    {
        _logger = logger;
        _recoveryThreshold = Math.Clamp(configuration.GetValue<int?>("Runtime:Redundancy:RecoveryThreshold") ?? 3, 1, 10);
        var enabled = string.Equals(configuration["Database:Provider"] ?? "PostgreSql", "PostgreSql", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration["Database:ConnectionStringSecondary"]);
        _state = new() { Enabled = enabled, Code = enabled ? "NoConflictObserved" : "NotRequired", Message = enabled ? "No cluster conflict has been observed." : "Cluster write protection is not required.", RequiredRecoveryChecks = _recoveryThreshold };
    }

    /// <summary>Возвращает готовое состояние без SQL и изменения счётчиков.</summary>
    public MainDatabaseWriteSafety GetStatus() => Volatile.Read(ref _state);

    /// <summary>Запрещает выдачу соединения; такая ошибка не является поводом попробовать другой сервер.</summary>
    public void ThrowIfBlocked()
    {
        var state = GetStatus();
        if (state.WritesBlocked)
            throw new MainDatabaseWriteBlockedException(state);
    }

    /// <summary>Принимает только готовый снимок монитора. Offline и устаревшие данные не снимают обнаруженный запрет.</summary>
    public void Observe(PostgreSqlClusterHealthResponse cluster, DateTimeOffset? observedAtUtc = null)
    {
        lock (_gate)
        {
            if (!_state.Enabled || !cluster.Enabled)
                return;

            var now = observedAtUtc ?? DateTimeOffset.UtcNow;
            var primary = cluster.Primary;
            var secondary = cluster.Secondary;
            var fresh = IsFresh(primary, cluster.HealthPeriodSeconds, now) && IsFresh(secondary, cluster.HealthPeriodSeconds, now);
            string? code = null;
            string? message = null;

            if (fresh && primary.Connected && secondary.Connected)
            {
                if (!string.IsNullOrWhiteSpace(primary.ServerAddress) && primary.ServerAddress == secondary.ServerAddress && primary.ServerPort is not null && primary.ServerPort == secondary.ServerPort)
                {
                    code = "DuplicateEndpoint";
                    message = "Primary and Secondary point to the same PostgreSQL endpoint. Work is blocked.";
                }
                else if (!string.IsNullOrWhiteSpace(primary.SystemIdentifier) && !string.IsNullOrWhiteSpace(secondary.SystemIdentifier) && primary.SystemIdentifier != secondary.SystemIdentifier)
                {
                    code = "ClusterMismatch";
                    message = "Primary and Secondary belong to different PostgreSQL clusters. Work is blocked.";
                }
                else if (primary.ActualRole == "Primary" && secondary.ActualRole == "Primary")
                {
                    code = "TwoPrimaries";
                    message = "Both PostgreSQL nodes report Primary. Work is blocked until one healthy Primary/Standby pair is restored.";
                }
            }

            if (code is not null)
            {
                var changed = !_state.WritesBlocked || _state.Code != code;
                _lastPrimaryCheck = primary.CheckedAtUtc;
                _lastSecondaryCheck = secondary.CheckedAtUtc;
                Volatile.Write(ref _state, _state with { WritesBlocked = true, Code = code, Message = message!, RecoveryChecks = 0, CheckedAtUtc = now });
                if (changed)
                    _logger.LogCritical("Main DB work blocked. Code={Code}.", code);
                return;
            }

            if (!_state.WritesBlocked)
                return;

            var healthyPair = fresh && IsHealthyPair(primary, secondary);
            var newSample = (_lastPrimaryCheck is null || primary.CheckedAtUtc > _lastPrimaryCheck) && (_lastSecondaryCheck is null || secondary.CheckedAtUtc > _lastSecondaryCheck);

            if (!healthyPair)
            {
                Volatile.Write(ref _state, _state with { RecoveryChecks = 0, CheckedAtUtc = now });
                if (_lastPrimaryCheck is null || primary.CheckedAtUtc > _lastPrimaryCheck)
                    _lastPrimaryCheck = primary.CheckedAtUtc;
                if (_lastSecondaryCheck is null || secondary.CheckedAtUtc > _lastSecondaryCheck)
                    _lastSecondaryCheck = secondary.CheckedAtUtc;
                return;
            }

            if (!newSample)
                return;

            _lastPrimaryCheck = primary.CheckedAtUtc;
            _lastSecondaryCheck = secondary.CheckedAtUtc;
            var checks = _state.RecoveryChecks + 1;

            if (checks < _recoveryThreshold)
            {
                Volatile.Write(ref _state, _state with { RecoveryChecks = checks, CheckedAtUtc = now });
                return;
            }

            Volatile.Write(ref _state, _state with { WritesBlocked = false, Code = "Ready", Message = "A healthy Primary/Standby pair was confirmed. Work is allowed.", RecoveryChecks = checks, CheckedAtUtc = now });
            _logger.LogInformation("Main DB work allowed after {Checks} healthy cluster checks.", checks);
        }
    }

    /// <summary>Проверяет возраст данных каждого настроенного узла, включая результаты неуспешного подключения.</summary>
    private static bool IsFresh(PostgreSqlServerState server, int period, DateTimeOffset now) => server.Configured && server.CheckedAtUtc is { } at && at <= now.AddSeconds(5) && now - at <= TimeSpan.FromSeconds(Math.Max(5, period) * 3d);

    /// <summary>Для снятия запрета нужна работающая репликация одного и того же физического кластера.</summary>
    private static bool IsHealthyPair(PostgreSqlServerState primary, PostgreSqlServerState secondary)
    {
        if (!primary.Connected || !secondary.Connected || !primary.MonitoringOk || !secondary.MonitoringOk || string.IsNullOrWhiteSpace(primary.SystemIdentifier) || primary.SystemIdentifier != secondary.SystemIdentifier)
            return false;

        var writer = primary.ActualRole == "Primary" ? primary : secondary;
        var standby = primary.ActualRole == "Standby" ? primary : secondary;
        return writer.ActualRole == "Primary" && writer.CanWrite && standby.ActualRole == "Standby" && !standby.CanWrite && standby.ReplayPaused == false && standby.ReceiverStatus == "streaming" && standby.SenderHost == writer.ServerAddress && !string.IsNullOrWhiteSpace(writer.ServerAddress) && writer.StreamingReplicaCount is > 0;
    }
}

/// <summary>Блокировка кластера распространяется на оба подключения и не допускает автоматического повтора.</summary>
public sealed class MainDatabaseWriteBlockedException(MainDatabaseWriteSafety safety) : InvalidOperationException(safety.Message)
{
    public MainDatabaseWriteSafety Safety { get; } = safety;
}
