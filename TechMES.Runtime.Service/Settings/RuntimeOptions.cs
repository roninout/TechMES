namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки самого Runtime.Service.
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>
    /// Стабильное имя узла для диагностики.
    /// Если пусто — используется имя компьютера.
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// Подготовительный режим Standby.
    /// HTTP-диагностика работает, рабочие модули не запускаются.
    /// Автоматический выбор владельца между компьютерами добавим следующим этапом.
    /// </summary>
    public bool StartInStandby { get; set; }

    /// <summary>
    /// Существующее имя устройства/сервиса.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// Существующее имя пользователя Runtime.
    /// </summary>
    public string UserName { get; set; } = "";
}