namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки heartbeat и ownership Calc.Service.
/// </summary>
public sealed class CalcServiceMonitorOptions
{
    /// <summary>
    /// После какого возраста heartbeat экземпляр считается Offline.
    /// </summary>
    public int OfflineAfterSeconds { get; set; } = 20;

    /// <summary>
    /// Сколько секунд один экземпляр владеет правом выполнения Jobs
    /// после успешного heartbeat.
    ///
    /// Каждый heartbeat владельца продлевает lease.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 15;
}