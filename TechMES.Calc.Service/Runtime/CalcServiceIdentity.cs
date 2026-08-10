namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Идентифицирует один конкретный запуск процесса TechMES.Calc.Service.
///
/// Новый запуск процесса всегда получает новый InstanceId.
/// </summary>
internal sealed class CalcServiceIdentity
{
    public CalcServiceIdentity()
    {
        MachineName = Environment.MachineName;
        ProcessId = Environment.ProcessId;
        StartedAtUtc = DateTimeOffset.UtcNow;
        InstanceId = $"{MachineName}:{ProcessId}:{Guid.NewGuid():N}";
        ServiceVersion = typeof(CalcServiceIdentity).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public string InstanceId { get; }
    public string MachineName { get; }
    public int ProcessId { get; }
    public string ServiceVersion { get; }
    public DateTimeOffset StartedAtUtc { get; }
}