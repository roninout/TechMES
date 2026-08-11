using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Результат одной операции Calc -> SCADA write.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcWriteAttemptStatusDto
{
    Success,
    Error
}

/// <summary>
/// Одна последняя попытка записи расчётного результата.
/// </summary>
public sealed class CalcWriteAttemptDto
{
    public DateTimeOffset AttemptedAtUtc { get; set; }

    public long JobId { get; set; }
    public string JobName { get; set; } = "";

    public string OutputKey { get; set; } = "";
    public string TagName { get; set; } = "";

    /// <summary>
    /// Исходное инженерное значение алгоритма.
    /// </summary>
    public double? RawValue { get; set; }

    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }

    /// <summary>
    /// Значение, которое фактически передавалось в TagWrite.
    /// </summary>
    public double? WrittenValue { get; set; }

    public CalcWriteAttemptStatusDto Status { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Runtime-диагностика Calc write после текущего запуска Runtime.Service.
/// </summary>
public sealed class CalcWriteDiagnosticsDto
{
    public bool Enabled { get; set; }

    public long AttemptCount { get; set; }
    public long SuccessCount { get; set; }
    public long ErrorCount { get; set; }

    public DateTimeOffset? LastAttemptAtUtc { get; set; }

    public List<CalcWriteAttemptDto> RecentAttempts { get; set; } = [];
}