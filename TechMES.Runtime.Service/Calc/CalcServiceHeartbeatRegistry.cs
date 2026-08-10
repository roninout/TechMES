using Microsoft.Extensions.Options;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Централизованно отслеживает экземпляры Calc.Service и выдаёт
/// lease только одному экземпляру одновременно.
///
/// Состояние намеренно хранится в памяти Runtime.
/// После перезапуска Runtime новый владелец выбирается заново.
/// </summary>
internal sealed class CalcServiceHeartbeatRegistry(IOptions<CalcServiceMonitorOptions> options)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HeartbeatSnapshot> _instances = new(StringComparer.Ordinal);

    private string? _leaseOwnerInstanceId;
    private long _leaseToken;
    private DateTimeOffset _leaseExpiresAtUtc;

    /// <summary>
    /// Регистрирует heartbeat, продлевает существующий lease
    /// либо выдаёт новый lease, если предыдущий закончился.
    /// </summary>
    public CalcServiceHeartbeatResponseDto RecordAndAcquire(CalcServiceHeartbeatRequest heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var nowUtc = DateTimeOffset.UtcNow;
        var instanceId = heartbeat.InstanceId.Trim();

        lock (_sync)
        {
            _instances[instanceId] = new HeartbeatSnapshot(
                instanceId,
                heartbeat.MachineName.Trim(),
                heartbeat.ProcessId,
                NormalizeOptional(heartbeat.ServiceVersion),
                heartbeat.StartedAtUtc,
                nowUtc);

            RemoveOfflineInstances(nowUtc);

            var leaseExpired = _leaseOwnerInstanceId is null || nowUtc >= _leaseExpiresAtUtc;

            if (string.Equals(_leaseOwnerInstanceId, instanceId, StringComparison.Ordinal) && !leaseExpired)
            {
                // Текущий владелец просто продлевает своё владение.
                _leaseExpiresAtUtc = nowUtc.AddSeconds(options.Value.LeaseDurationSeconds);
            }
            else if (leaseExpired)
            {
                // Старый lease закончился. Новый владелец получает новый fencing token.
                _leaseOwnerInstanceId = instanceId;
                _leaseToken = NextLeaseToken(_leaseToken);
                _leaseExpiresAtUtc = nowUtc.AddSeconds(options.Value.LeaseDurationSeconds);
            }

            return BuildHeartbeatResponse(instanceId, nowUtc);
        }
    }

    /// <summary>
    /// Проверяет, имеет ли конкретный экземпляр право выполнять
    /// защищённое действие именно с указанным fencing token.
    /// </summary>
    public bool IsLeaseOwner(string? instanceId, long leaseToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || leaseToken <= 0)
            return false;

        lock (_sync)
        {
            var nowUtc = DateTimeOffset.UtcNow;

            return nowUtc < _leaseExpiresAtUtc
                && _leaseToken == leaseToken
                && string.Equals(_leaseOwnerInstanceId, instanceId.Trim(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Возвращает состояние текущего активного владельца для WEB.
    /// </summary>
    public CalcServiceStatusDto GetStatus()
    {
        lock (_sync)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            RemoveOfflineInstances(nowUtc);

            /*
             * Если владельца нет, lease истёк или запись владельца
             * уже отсутствует среди активных экземпляров, Calc.Service
             * считаем Offline.
             *
             * Такая форма проверки также гарантирует компилятору,
             * что после этого блока переменная owner уже инициализирована.
             */
            if (_leaseOwnerInstanceId is null
                || nowUtc >= _leaseExpiresAtUtc
                || !_instances.TryGetValue(_leaseOwnerInstanceId, out var owner))
            {
                return new CalcServiceStatusDto
                {
                    Availability = CalcServiceAvailabilityDto.Offline,
                    ActiveInstanceCount = _instances.Count,
                    OfflineAfterSeconds = options.Value.OfflineAfterSeconds,
                    LeaseDurationSeconds = options.Value.LeaseDurationSeconds
                };
            }

            var ageSeconds = Math.Max(0L, (long)Math.Floor((nowUtc - owner.ReceivedAtUtc).TotalSeconds));

            return new CalcServiceStatusDto
            {
                Availability = CalcServiceAvailabilityDto.Online,
                InstanceId = owner.InstanceId,
                MachineName = owner.MachineName,
                ProcessId = owner.ProcessId,
                ServiceVersion = owner.ServiceVersion,
                StartedAtUtc = owner.StartedAtUtc,
                LastHeartbeatReceivedAtUtc = owner.ReceivedAtUtc,
                HeartbeatAgeSeconds = ageSeconds,
                LeaseToken = _leaseToken,
                LeaseExpiresAtUtc = _leaseExpiresAtUtc,
                ActiveInstanceCount = _instances.Count,
                OfflineAfterSeconds = options.Value.OfflineAfterSeconds,
                LeaseDurationSeconds = options.Value.LeaseDurationSeconds
            };
        }
    }

    /// <summary>
    /// Формирует ответ конкретному экземпляру Calc.Service.
    /// </summary>
    private CalcServiceHeartbeatResponseDto BuildHeartbeatResponse(string instanceId, DateTimeOffset nowUtc)
    {
        var remaining = Math.Max(0L, (long)(_leaseExpiresAtUtc - nowUtc).TotalMilliseconds);

        return new CalcServiceHeartbeatResponseDto
        {
            InstanceId = instanceId,
            IsLeaseOwner = string.Equals(_leaseOwnerInstanceId, instanceId, StringComparison.Ordinal)
                && nowUtc < _leaseExpiresAtUtc,
            LeaseOwnerInstanceId = _leaseOwnerInstanceId,
            LeaseToken = _leaseToken,
            LeaseExpiresAtUtc = _leaseOwnerInstanceId is null ? null : _leaseExpiresAtUtc,
            LeaseRemainingMilliseconds = remaining,
            LeaseDurationSeconds = options.Value.LeaseDurationSeconds
        };
    }

    /// <summary>
    /// Удаляет давно не видимые экземпляры из диагностического списка.
    ///
    /// Сам lease отдельно ограничен LeaseDurationSeconds и поэтому
    /// может закончиться раньше OfflineAfterSeconds.
    /// </summary>
    private void RemoveOfflineInstances(DateTimeOffset nowUtc)
    {
        var maximumAge = TimeSpan.FromSeconds(options.Value.OfflineAfterSeconds);

        var expiredIds = _instances
            .Where(item => nowUtc - item.Value.ReceivedAtUtc > maximumAge)
            .Select(item => item.Key)
            .ToList();

        foreach (var instanceId in expiredIds)
            _instances.Remove(instanceId);

        if (_leaseOwnerInstanceId is not null
            && !_instances.ContainsKey(_leaseOwnerInstanceId)
            && nowUtc >= _leaseExpiresAtUtc)
        {
            _leaseOwnerInstanceId = null;
        }
    }

    private static long NextLeaseToken(long current)
    {
        // practically unreachable, но не допускаем переход long в отрицательное значение.
        return current == long.MaxValue ? 1 : current + 1;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed record HeartbeatSnapshot(
        string InstanceId,
        string MachineName,
        int ProcessId,
        string? ServiceVersion,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ReceivedAtUtc);
}