namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки API конфигурации расчётных заданий.
///
/// Отдельный выключатель предотвращает случайное изменение
/// конфигурации на промышленном сервере.
/// </summary>
public sealed class CalcConfigurationOptions
{
    /// <summary>
    /// Разрешает POST, PUT и DELETE для /api/calc/jobs.
    /// GET и ручной Calc Test остаются доступными всегда.
    /// </summary>
    public bool EditingEnabled { get; set; }
}