namespace TechMES.Contracts.Runtime;

/// <summary>
/// Ответ Runtime.Service на health-запрос.
/// Сохраняет существующий contract и добавляет состояние активности.
/// </summary>
public sealed class RuntimeHealthResponse
{
    /// <summary>
    /// Доступность процесса и разрешение выполнять работу — разные признаки.
    /// </summary>
    public RuntimeActivityStatusResponse? Activity { get; set; }

    public string Status { get; set; } = "Unknown";
    public string Service { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string MessageStorageProvider { get; set; } = "";
    public string Database { get; set; } = "Unknown";
    public int ActiveMessageCount { get; set; }
    public DateTime Time { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Существующий признак готовности для WEB.
    /// Standby/Starting/Faulted не считаются готовыми к рабочим запросам.
    /// </summary>
    public bool IsHealthy => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase) && string.Equals(Database, "OK", StringComparison.OrdinalIgnoreCase);
}