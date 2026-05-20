namespace TechMES.Runtime.Service.Runtime;

/// <summary>
/// Общий runtime-контекст приложения.
/// 
/// Через этот интерфейс разные части Runtime.Service получают имя устройства,
/// пользователя и версию приложения, не читая appsettings напрямую.
/// </summary>
public interface IAppRuntimeContext
{
    string DeviceName { get; }

    string UserName { get; }

    string MachineName { get; }

    string AppVersion { get; }
}