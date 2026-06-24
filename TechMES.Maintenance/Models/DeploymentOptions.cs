namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки развертывания серверной части TechMES.
/// Эти значения задают, куда публиковать WEB/Runtime и какими параметрами запускать dotnet publish.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>
    /// Корневая папка сервера, куда будут публиковаться сервисы.
    /// Например: C:\TechMES.
    /// </summary>
    public string PublishRoot { get; set; } = @"C:\TechMES";

    /// <summary>
    /// Конфигурация сборки для dotnet publish.
    /// Обычно Release.
    /// </summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>
    /// Runtime identifier для Windows-сервера.
    /// win-x64 оставляем дефолтом, потому что серверная машина почти наверняка x64.
    /// </summary>
    public string RuntimeIdentifier { get; set; } = "win-x64";

    /// <summary>
    /// Публиковать приложение self-contained или framework-dependent.
    /// По умолчанию false: на сервере должен быть установлен нужный .NET Runtime.
    /// </summary>
    public bool SelfContained { get; set; }

    /// <summary>
    /// Устанавливать Windows Services с автоматическим запуском.
    /// </summary>
    public bool AutoStart { get; set; } = true;
}
