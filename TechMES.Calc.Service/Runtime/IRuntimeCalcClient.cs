using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Исходящий клиент Calc.Service к единой точке Runtime.Service.
/// </summary>
public interface IRuntimeCalcClient
{
    Task<CalcConfigurationSnapshotDto> GetConfigurationSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Пакетно читает уникальные SCADA-теги.
    /// </summary>
    Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default);

    /// <summary>
    /// Передаёт диагностические результаты shadow-расчётов.
    /// </summary>
    Task<CalcExecutionResultBatchResponse> SaveExecutionResultsAsync(CalcExecutionResultBatchRequest request, CancellationToken ct = default);
}