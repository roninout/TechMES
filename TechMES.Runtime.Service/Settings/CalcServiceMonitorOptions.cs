namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки контроля доступности TechMES.Calc.Service.
/// </summary>
public sealed class CalcServiceMonitorOptions
{
    /// <summary>
    /// После какого возраста последнего heartbeat служба считается Offline.
    /// </summary>
    public int OfflineAfterSeconds { get; set; } = 20;
}