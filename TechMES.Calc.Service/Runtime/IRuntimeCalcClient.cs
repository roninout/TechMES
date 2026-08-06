using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Исходящий клиент Calc.Service к Runtime.Service.
///
/// Calc.Service не получает прямой доступ к PostgreSQL или CtApi.
/// </summary>
public interface IRuntimeCalcClient
{
    Task<CalcConfigurationSnapshotDto> GetConfigurationSnapshotAsync(CancellationToken ct = default);
}