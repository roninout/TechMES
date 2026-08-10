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
///
/// Одновременно используется для передачи Calc.Service
/// текущего состояния lease.
/// </summary>
public sealed class CalcServiceHeartbeatResponseDto
{
    /// <summary>
    /// InstanceId отправителя heartbeat.
    /// </summary>
    public string InstanceId { get; set; } = "";

    /// <summary>
    /// true только для экземпляра, которому сейчас разрешено выполнять Jobs.
    /// </summary>
    public bool IsLeaseOwner { get; set; }

    /// <summary>
    /// InstanceId текущего владельца lease.
    /// </summary>
    public string? LeaseOwnerInstanceId { get; set; }

    /// <summary>
    /// Fencing token текущего lease.
    ///
    /// При каждом новом захвате lease значение увеличивается.
    /// </summary>
    public long LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    /// <summary>
    /// Оставшееся время lease по часам Runtime.
    /// </summary>
    public long LeaseRemainingMilliseconds { get; set; }

    public int LeaseDurationSeconds { get; set; }
}

/// <summary>
/// Текущее состояние Calc.Service с точки зрения Runtime.Service.
/// </summary>
public sealed class CalcServiceStatusDto
{
    public CalcServiceAvailabilityDto Availability { get; set; } = CalcServiceAvailabilityDto.Offline;

    /// <summary>
    /// Данные текущего владельца lease.
    /// </summary>
    public string? InstanceId { get; set; }
    public string? MachineName { get; set; }
    public int? ProcessId { get; set; }
    public string? ServiceVersion { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatReceivedAtUtc { get; set; }
    public long? HeartbeatAgeSeconds { get; set; }

    public long? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    /// <summary>
    /// Количество недавно видимых экземпляров Calc.Service.
    /// </summary>
    public int ActiveInstanceCount { get; set; }

    public int OfflineAfterSeconds { get; set; }
    public int LeaseDurationSeconds { get; set; }
}