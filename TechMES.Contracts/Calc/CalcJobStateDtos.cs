using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Текущее состояние выполнения расчётного задания.
/// Значения соответствуют ограничению calc_job_state.status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcJobStateStatusDto
{
    NeverRun,
    Running,
    Success,
    Skipped,
    Error
}

/// <summary>
/// Пакет результатов shadow-расчётов, отправляемый Calc.Service в Runtime.
/// </summary>
public sealed class CalcExecutionResultBatchRequest
{
    public string ServiceInstanceId { get; set; } = "";

    /// <summary>
    /// Runtime epoch, в котором был получен LeaseToken.
    /// </summary>
    public string LeaseEpoch { get; set; } = "";

    /// <summary>
    /// Fencing token внутри LeaseEpoch.
    /// </summary>
    public long LeaseToken { get; set; }

    public DateTimeOffset SubmittedAtUtc { get; set; }
    public List<CalcExecutionResultItemDto> Items { get; set; } = [];
}

/// <summary>
/// Результат одной попытки выполнения задания.
/// </summary>
public sealed class CalcExecutionResultItemDto
{
    public long JobId { get; set; }
    public long ConfigurationRevision { get; set; }
    public long ServiceCycleNumber { get; set; }
    public string DefinitionCode { get; set; } = "";
    public string DefinitionVersion { get; set; } = "";
    public CalcJobStateStatusDto Status { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonMessage { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }

    /// <summary>
    /// Фактические значения, переданные в TechMES.Calc.
    /// Должен содержать JSON object.
    /// </summary>
    public JsonElement Inputs { get; set; }

    /// <summary>
    /// Исходные инженерные результаты алгоритма.
    ///
    /// Scale и Offset здесь ещё НЕ применены.
    /// Преобразование для целевого SCADA-тега выполняет Runtime.Service
    /// непосредственно перед контролируемым TagWrite.
    /// </summary>
    public JsonElement Outputs { get; set; }
}

/// <summary>
/// Ответ Runtime на сохранение пакета результатов.
/// Логическое отклонение одного результата не отменяет остальные.
/// </summary>
public sealed class CalcExecutionResultBatchResponse
{
    public int RequestedCount { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public List<CalcExecutionResultSaveResponse> Items { get; set; } = [];
}

/// <summary>
/// Результат сохранения одного job.
/// </summary>
public sealed class CalcExecutionResultSaveResponse
{
    public long JobId { get; set; }
    public long ServiceCycleNumber { get; set; }
    public bool Accepted { get; set; }
    public long? PersistedCycleNumber { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Сохранённое диагностическое состояние одного задания.
/// </summary>
public sealed class CalcJobStateDto
{
    public long JobId { get; set; }
    public string JobName { get; set; } = "";
    public string? EquipmentName { get; set; }
    public CalcJobStateStatusDto Status { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonMessage { get; set; }
    public string? DefinitionVersion { get; set; }
    public long? ConfigurationRevision { get; set; }

    /// <summary>
    /// Общее количество результатов, принятых PostgreSQL после
    /// последнего изменения конфигурации.
    /// </summary>
    public long CycleNumber { get; set; }

    /// <summary>
    /// Счётчики результатов текущей Revision.
    /// Сбрасываются при изменении конфигурации Job.
    /// </summary>
    public long SuccessCount { get; set; }
    public long SkippedCount { get; set; }
    public long ErrorCount { get; set; }

    /// <summary>
    /// Количество последовательных Skipped или Error.
    /// Другой результат сбрасывает соответствующий счётчик.
    /// </summary>
    public long ConsecutiveSkippedCount { get; set; }
    public long ConsecutiveErrorCount { get; set; }

    public DateTimeOffset? LastStartedAtUtc { get; set; }
    public DateTimeOffset? LastCompletedAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public long? LastDurationMs { get; set; }
    public JsonElement? LastInputs { get; set; }
    public JsonElement? LastOutputs { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Ответ со состояниями всех расчётных заданий.
/// </summary>
public sealed class CalcJobStatesResponse
{
    public List<CalcJobStateDto> Items { get; set; } = [];
}