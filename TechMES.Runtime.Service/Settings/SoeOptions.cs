namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки SOE endpoint-а.
/// </summary>
public sealed class SoeOptions
{
    /// <summary>
    /// Максимум строк, которые можно прочитать из одного trend tag-а.
    /// </summary>
    public int PerTrendMax { get; set; } = 1000;

    /// <summary>
    /// Общий максимум строк SOE в одном ответе.
    /// </summary>
    public int TotalMax { get; set; } = 100;
}
