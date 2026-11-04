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
    /// Тестовый Standby для одиночного Runtime при Redundancy.Enabled = false.
    /// При включённом резервировании этот флаг игнорируется: mode назначает координатор.
    /// </summary>
    public bool StartInStandby { get; set; }

    /// <summary>
    /// Параметры автоматического выбора активного Runtime.
    /// </summary>
    public RuntimeRedundancyOptions Redundancy { get; set; } = new();

    /// <summary>
    /// Существующее имя устройства/сервиса.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// Существующее имя пользователя Runtime.
    /// </summary>
    public string UserName { get; set; } = "";
}