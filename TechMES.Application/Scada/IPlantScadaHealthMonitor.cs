namespace TechMES.Application.Scada;

/// <summary>
/// Активная проверка выполняется worker-ом.
/// HTTP health читает готовый снимок.
/// </summary>
public interface IPlantScadaHealthMonitor
{
    Task RefreshHealthAsync(CancellationToken ct = default);
}