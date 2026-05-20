namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки самого Runtime.Service.
/// 
/// Эти значения читаются из appsettings.json.
/// Позже WPF Configurator сможет менять эти настройки.
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>
    /// Имя устройства/сервиса.
    /// Если пусто — будет использовано имя Windows-машины.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// Имя пользователя Runtime.Service.
    /// Пока можно оставить пустым.
    /// Позже сюда можно будет подставлять пользователя CtApi или системного оператора.
    /// </summary>
    public string UserName { get; set; } = "";
}