namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Настройки модуля Messages в Runtime.Service.
/// </summary>
public sealed class MessagesOptions
{
    /// <summary>
    /// Включить фоновую проверку хранилища сообщений.
    ///
    /// SignalR сам сообщает о действиях, которые прошли через Runtime.Service.
    /// Но если данные изменили напрямую в БД, Runtime.Service узнает об этом
    /// только через этот watcher.
    /// </summary>
    public bool EnableStorageWatcher { get; set; } = true;

    /// <summary>
    /// Период проверки БД в секундах.
    ///
    /// Аналог идеи RefreshPeriodSeconds из WPF,
    /// но теперь проверку делает не WEB UI, а Runtime.Service.
    /// </summary>
    public int RefreshPeriodSeconds { get; set; } = 30;
}