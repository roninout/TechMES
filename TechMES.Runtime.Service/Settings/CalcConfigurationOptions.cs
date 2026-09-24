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
    /// GET каталога и API проверки расчётов остаются доступны рабочим панелям.
    /// </summary>
    public bool EditingEnabled { get; set; }
}