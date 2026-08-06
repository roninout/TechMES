using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Execution;

/// <summary>
/// Итоговый статус одной попытки выполнения задания.
/// </summary>
internal enum CalcJobExecutionStatus
{
    Success,
    Skipped,
    Error
}

/// <summary>
/// Запрос выполнения одного задания внутри общего shadow-цикла.
/// </summary>
internal sealed record CalcJobExecutionRequest(CalcExecutionJobDto Job, long CycleNumber);

/// <summary>
/// Результат выполнения одного задания.
///
/// Позже эта модель станет источником данных для сохранения
/// calc_job_state через Runtime.Service.
/// </summary>
internal sealed class CalcJobExecutionResult
{
    public long JobId { get; init; }
    public long Revision { get; init; }
    public long CycleNumber { get; init; }
    public string JobName { get; init; } = "";
    public string DefinitionCode { get; init; } = "";
    public CalcJobExecutionStatus Status { get; init; }
    public string DefinitionVersion { get; init; } = "";
    public string? ReasonCode { get; init; }
    public string? ReasonMessage { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }

    /// <summary>
    /// Фактические значения, переданные расчётному ядру.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Inputs { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Рассчитанные значения после применения Scale и Offset.
    /// </summary>
    public IReadOnlyDictionary<string, double> Outputs { get; init; } =
        new Dictionary<string, double>();
}