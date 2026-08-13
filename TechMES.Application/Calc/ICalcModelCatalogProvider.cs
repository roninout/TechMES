using TechMES.Contracts.Calc;

namespace TechMES.Application.Calc;

/// <summary>
/// Порт Runtime для SCADA calculation catalog.
///
/// Важно:
/// Runtime не загружает этот каталог при собственном старте.
/// Reload вызывается Calc.Service после получения lease
/// либо вручную через WEB Refresh.
/// </summary>
public interface ICalcModelCatalogProvider
{
    /// <summary>
    /// Возвращает только текущий in-memory snapshot.
    /// Никогда самостоятельно не запускает CtApi scan.
    /// </summary>
    Task<CalcModelCatalogResponse> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Явно перестраивает каталог через Plant SCADA.
    /// </summary>
    Task<CalcModelCatalogResponse> ReloadAsync(CancellationToken ct = default);
}