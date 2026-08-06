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
    /// Пакетно читает уникальные входные теги активной конфигурации.
    /// </summary>
    Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default);
}