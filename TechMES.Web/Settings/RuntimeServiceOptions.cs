namespace TechMES.Web.Settings;

/// <summary>
/// Настройки HTTP-подключений WEB к Runtime Service.
/// </summary>
public sealed class RuntimeServiceOptions
{
    /// <summary>
    /// Первый адрес Runtime.Service.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5101/";

    /// <summary>
    /// Второй адрес Runtime.Service. Пустое значение отключает второй адрес.
    /// </summary>
    public string SecondaryBaseUrl { get; set; } = "";

    /// <summary>
    /// Общий таймаут HTTP-запроса, включая выбор активного Runtime.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Путь SignalR Hub. Переключение SignalR реализуем в пункте 19.
    /// </summary>
    public string MessagesHubPath { get; set; } = "hubs/messages";
}