using TechMES.Contracts.Calc;

namespace TechMES.Application.Calc;

/// <summary>
/// Хранилище текущих состояний расчётных заданий.
/// </summary>
public interface ICalcJobStateStore
{
    /// <summary>
    /// Возвращает состояния всех заданий.
    /// </summary>
    Task<IReadOnlyList<CalcJobStateDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Возвращает состояние одного задания или null.
    /// </summary>
    Task<CalcJobStateDto?> GetAsync(long jobId, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет пакет результатов shadow-расчётов.
    ///
    /// Результат принимается только для существующего enabled-задания,
    /// если Revision, definition code и version ещё совпадают.
    /// </summary>
    Task<CalcExecutionResultBatchResponse> SaveResultsAsync(CalcExecutionResultBatchRequest request, CancellationToken ct = default);
}