using Microsoft.Extensions.Options;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Хранит последний heartbeat Calc.Service только в памяти Runtime.
///
/// Это намеренно не PostgreSQL-state: после перезапуска Runtime служба
/// считается Offline до получения нового heartbeat.
/// </summary>
internal sealed class CalcServiceHeartbeatRegistry(IOptions<CalcServiceMonitorOptions> options)
{
    private readonly object _sync = new();
    private HeartbeatSnapshot? _last;

    /// <summary>
    /// Принимает новый heartbeat и фиксирует время его получения Runtime.
    /// </summary>
    public void Record(CalcServiceHeartbeatRequest heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var snapshot = new HeartbeatSnapshot(
            heartbeat.InstanceId.Trim(),
            heartbeat.MachineName.Trim(),
            heartbeat.ProcessId,
            NormalizeOptional(heartbeat.ServiceVersion),
            heartbeat.StartedAtUtc,
            DateTimeOffset.UtcNow);

        lock (_sync)
            _last = snapshot;
    }

    /// <summary>
    /// Возвращает Online/Offline на основании возраста последнего heartbeat.
    /// </summary>
    public CalcServiceStatusDto GetStatus()
    {
        HeartbeatSnapshot? snapshot;

        lock (_sync)
            snapshot = _last;

        var offlineAfterSeconds = options.Value.OfflineAfterSeconds;

        if (snapshot is null)
        {
            return new CalcServiceStatusDto
            {
                Availability = CalcServiceAvailabilityDto.Offline,
                OfflineAfterSeconds = offlineAfterSeconds
            };
        }

        var age = DateTimeOffset.UtcNow - snapshot.ReceivedAtUtc;
        var ageSeconds = Math.Max(0L, (long)Math.Floor(age.TotalSeconds));

        return new CalcServiceStatusDto
        {
            Availability = ageSeconds <= offlineAfterSeconds
                ? CalcServiceAvailabilityDto.Online
                : CalcServiceAvailabilityDto.Offline,

            InstanceId = snapshot.InstanceId,
            MachineName = snapshot.MachineName,
            ProcessId = snapshot.ProcessId,
            ServiceVersion = snapshot.ServiceVersion,
            StartedAtUtc = snapshot.StartedAtUtc,
            LastHeartbeatReceivedAtUtc = snapshot.ReceivedAtUtc,
            HeartbeatAgeSeconds = ageSeconds,
            OfflineAfterSeconds = offlineAfterSeconds
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed record HeartbeatSnapshot(string InstanceId, string MachineName, int ProcessId, string? ServiceVersion, DateTimeOffset StartedAtUtc, DateTimeOffset ReceivedAtUtc);
}