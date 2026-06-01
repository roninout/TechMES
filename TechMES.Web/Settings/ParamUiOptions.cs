namespace TechMES.Web.Settings;

/// <summary>
/// Настройки UI Param-модуля в Blazor Web.
/// </summary>
public sealed class ParamUiOptions
{
    /// <summary>
    /// Видимый live-диапазон тренда в минутах.
    /// </summary>
    public int TrendWindowMinutes { get; set; } = 30;

    /// <summary>
    /// Исторический диапазон, который WEB запрашивает заранее для плавной прокрутки графика.
    /// </summary>
    public int TrendHistoryMinutes { get; set; } = 60;
}
