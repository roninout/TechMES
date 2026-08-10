using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Текущее состояние доступности TechMES.Calc.Service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcServiceAvailabilityDto
{
    Offline,
    Online
}

/// <summary>
/// Heartbeat одного конкретного экземпляра Calc.Service.
/// </summary>
public sealed class CalcServiceHeartbeatRequest
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int ProcessId { get; set; }
    public string? ServiceVersion { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
}

/// <summary>
/// Ответ Runtime на heartbeat.
/// Одновременно передаёт текущее состояние execution lease.
/// </summary>
public sealed class CalcServiceHeartbeatResponseDto
{
    public string InstanceId { get; set; } = "";
    public bool IsLeaseOwner { get; set; }
    public string? LeaseOwnerInstanceId { get; set; }

    /// <summary>
    /// Уникальный идентификатор поколения lease.
    /// Меняется при каждом запуске Runtime.Service.
    /// </summary>
    public string LeaseEpoch { get; set; } = "";

    /// <summary>
    /// Последовательный fencing token внутри одного Runtime epoch.
    /// </summary>
    public long LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseRemainingMilliseconds { get; set; }
    public int LeaseDurationSeconds { get; set; }
}

/// <summary>
/// Текущее состояние Calc.Service с точки зрения Runtime.Service.
/// </summary>
public sealed class CalcServiceStatusDto
{
    public CalcServiceAvailabilityDto Availability { get; set; } = CalcServiceAvailabilityDto.Offline;
    public string? InstanceId { get; set; }
    public string? MachineName { get; set; }
    public int? ProcessId { get; set; }
    public string? ServiceVersion { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatReceivedAtUtc { get; set; }
    public long? HeartbeatAgeSeconds { get; set; }

    public string? LeaseEpoch { get; set; }
    public long? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    public int ActiveInstanceCount { get; set; }
    public int OfflineAfterSeconds { get; set; }
    public int LeaseDurationSeconds { get; set; }
}