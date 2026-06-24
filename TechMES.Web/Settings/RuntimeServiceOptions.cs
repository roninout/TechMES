namespace TechMES.Web.Settings;

/// <summary>
/// Настройки подключения WEB-приложения к Runtime Service.
///
/// Blazor Web не работает с CtApi и БД напрямую.
/// Он отправляет HTTP-запросы в Runtime Service по BaseUrl.
/// Для live-обновлений он также подключается к SignalR Hub.
/// </summary>
public sealed class RuntimeServiceOptions
{
    /// <summary>
    /// Базовый адрес Runtime.Service.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5101/";

    /// <summary>
    /// Таймаут обычных HTTP-запросов.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Относительный путь SignalR Hub для Messages.
    /// </summary>
    public string MessagesHubPath { get; set; } = "hubs/messages";
}
