using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Исходящий клиент Calc.Service к единой точке Runtime.Service.
/// </summary>
public interface IRuntimeCalcClient
{
    /// <summary>
    /// Явно просит Runtime перечитать Calc models через CtApi.
    /// </summary>
    Task<CalcModelCatalogResponse> RefreshModelCatalogAsync(CancellationToken ct = default);
    Task<CalcConfigurationSnapshotDto> GetConfigurationSnapshotAsync(CancellationToken ct = default);
    Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default);
    Task<CalcExecutionResultBatchResponse> SaveExecutionResultsAsync(CalcExecutionResultBatchRequest request, CancellationToken ct = default);
    Task<CalcServiceHeartbeatResponseDto> SendHeartbeatAsync(CalcServiceHeartbeatRequest request, CancellationToken ct = default);
}