namespace TechMES.Calc.Service.Settings;

/// <summary>
/// Настройки исходящего соединения Calc.Service с Runtime.Service.
/// </summary>
public sealed class CalcRuntimeClientOptions
{
    /// <summary>
    /// Базовый HTTP-адрес единой точки доступа TechMES.Runtime.Service.
    /// </summary>
    public string BaseAddress { get; set; } = "http://127.0.0.1:5101/";

    /// <summary>
    /// Timeout одного HTTP-запроса.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Период обновления configuration snapshot.
    /// </summary>
    public int ConfigurationRefreshSeconds { get; set; } = 30;
}