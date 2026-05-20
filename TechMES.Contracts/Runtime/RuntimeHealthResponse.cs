namespace TechMES.Contracts.Runtime;

/// <summary>
/// Ответ Runtime.Service на health-запрос.
///
/// Этот contract используется WEB-приложением,
/// чтобы показать состояние Runtime.Service в верхней панели.
/// </summary>
public sealed class RuntimeHealthResponse
{
    /// <summary>
    /// Общий статус Runtime.Service.
    /// Обычно "OK" или "ERROR".
    /// </summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// Имя сервиса.
    /// </summary>
    public string Service { get; set; } = "";

    /// <summary>
    /// Имя устройства Runtime.Service.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// Имя runtime-пользователя.
    /// </summary>
    public string UserName { get; set; } = "";

    /// <summary>
    /// Имя Windows-машины, на которой запущен Runtime.Service.
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// Версия Runtime.Service.
    /// </summary>
    public string AppVersion { get; set; } = "";

    /// <summary>
    /// Какой adapter сейчас используется для Messages.
    /// Например: InMemory или PostgreSql.
    /// </summary>
    public string MessageStorageProvider { get; set; } = "";

    /// <summary>
    /// Статус подключения к БД / хранилищу.
    /// </summary>
    public string Database { get; set; } = "Unknown";

    /// <summary>
    /// Количество активных сообщений.
    /// </summary>
    public int ActiveMessageCount { get; set; }

    /// <summary>
    /// Время ответа Runtime.Service.
    /// </summary>
    public DateTime Time { get; set; }

    /// <summary>
    /// Текст ошибки, если health check не прошёл.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Удобный computed-флаг для UI.
    /// </summary>
    public bool IsHealthy =>
        string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Database, "OK", StringComparison.OrdinalIgnoreCase);
}