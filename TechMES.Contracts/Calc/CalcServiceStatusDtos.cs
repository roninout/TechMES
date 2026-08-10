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
/// Heartbeat, который Calc.Service периодически отправляет Runtime.Service.
/// Время получения фиксируется самим Runtime и используется для определения liveness.
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
    public int OfflineAfterSeconds { get; set; }
}