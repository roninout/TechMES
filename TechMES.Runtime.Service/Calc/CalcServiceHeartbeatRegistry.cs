using Microsoft.Extensions.Options;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Централизованно отслеживает экземпляры Calc.Service и выдаёт
/// execution lease только одному экземпляру одновременно.
///
/// LeaseEpoch уникален для текущего запуска Runtime.Service.
/// </summary>
internal sealed class CalcServiceHeartbeatRegistry(IOptions<CalcServiceMonitorOptions> options)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HeartbeatSnapshot> _instances = new(StringComparer.Ordinal);
    private readonly string _leaseEpoch = Guid.NewGuid().ToString("N");

    private string? _leaseOwnerInstanceId;
    private long _leaseToken;
    private DateTimeOffset _leaseExpiresAtUtc;

    /// <summary>
    /// Регистрирует heartbeat, продлевает текущий lease
    /// либо выдаёт новый lease после завершения предыдущего.
    /// </summary>
    public CalcServiceHeartbeatResponseDto RecordAndAcquire(CalcServiceHeartbeatRequest heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var nowUtc = DateTimeOffset.UtcNow;
        var instanceId = heartbeat.InstanceId.Trim();

        lock (_sync)
        {
            _instances[instanceId] = new HeartbeatSnapshot(
                instanceId, heartbeat.MachineName.Trim(), heartbeat.ProcessId,
                NormalizeOptional(heartbeat.ServiceVersion), heartbeat.StartedAtUtc, nowUtc);

            RemoveOfflineInstances(nowUtc);

            var leaseExpired = _leaseOwnerInstanceId is null || nowUtc >= _leaseExpiresAtUtc;

            if (string.Equals(_leaseOwnerInstanceId, instanceId, StringComparison.Ordinal) && !leaseExpired)
            {
                // Текущий владелец продлевает существующий lease.
                _leaseExpiresAtUtc = nowUtc.AddSeconds(options.Value.LeaseDurationSeconds);
            }
            else if (leaseExpired)
            {
                // Новый ownership получает следующий token текущего Runtime epoch.
                _leaseOwnerInstanceId = instanceId;
                _leaseToken = NextLeaseToken(_leaseToken);
                _leaseExpiresAtUtc = nowUtc.AddSeconds(options.Value.LeaseDurationSeconds);
            }

            return BuildHeartbeatResponse(instanceId, nowUtc);
        }
    }

    /// <summary>
    /// Проверяет полный fencing identity защищённого действия.
    /// </summary>
    public bool IsLeaseOwner(string? instanceId, string? leaseEpoch, long leaseToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(leaseEpoch) || leaseToken <= 0)
            return false;

        lock (_sync)
        {
            var nowUtc = DateTimeOffset.UtcNow;

            return nowUtc < _leaseExpiresAtUtc
                && _leaseToken == leaseToken
                && string.Equals(_leaseEpoch, leaseEpoch.Trim(), StringComparison.Ordinal)
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

            if (_leaseOwnerInstanceId is null
                || nowUtc >= _leaseExpiresAtUtc
                || !_instances.TryGetValue(_leaseOwnerInstanceId, out var owner))
            {
                return new CalcServiceStatusDto
                {
                    Availability = CalcServiceAvailabilityDto.Offline,
                    LeaseEpoch = _leaseEpoch,
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
                LeaseEpoch = _leaseEpoch,
                LeaseToken = _leaseToken,
                LeaseExpiresAtUtc = _leaseExpiresAtUtc,
                ActiveInstanceCount = _instances.Count,
                OfflineAfterSeconds = options.Value.OfflineAfterSeconds,
                LeaseDurationSeconds = options.Value.LeaseDurationSeconds
            };
        }
    }

    private CalcServiceHeartbeatResponseDto BuildHeartbeatResponse(string instanceId, DateTimeOffset nowUtc)
    {
        var remaining = Math.Max(0L, (long)(_leaseExpiresAtUtc - nowUtc).TotalMilliseconds);

        return new CalcServiceHeartbeatResponseDto
        {
            InstanceId = instanceId,
            IsLeaseOwner = string.Equals(_leaseOwnerInstanceId, instanceId, StringComparison.Ordinal)
                && nowUtc < _leaseExpiresAtUtc,
            LeaseOwnerInstanceId = _leaseOwnerInstanceId,
            LeaseEpoch = _leaseEpoch,
            LeaseToken = _leaseToken,
            LeaseExpiresAtUtc = _leaseOwnerInstanceId is null ? null : _leaseExpiresAtUtc,
            LeaseRemainingMilliseconds = remaining,
            LeaseDurationSeconds = options.Value.LeaseDurationSeconds
        };
    }

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
        return current == long.MaxValue ? 1 : current + 1;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed record HeartbeatSnapshot(
        string InstanceId, string MachineName, int ProcessId, string? ServiceVersion,
        DateTimeOffset StartedAtUtc, DateTimeOffset ReceivedAtUtc);
}