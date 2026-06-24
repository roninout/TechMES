namespace TechMES.Maintenance.Models;

/// <summary>
/// Описание одного управляемого сервиса TechMES.
/// Отдельно храним display name для интерфейса и service name для Windows Service Control Manager.
/// </summary>
public sealed class ServiceDefinition
{
    /// <summary>
    /// Стабильный ключ сервиса внутри Maintenance.
    /// Нужен, чтобы позднее хранить индивидуальные настройки, не привязываясь к отображаемому тексту.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Человекочитаемое имя, которое видно в таблице Maintenance.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Имя Windows Service, которое передается в sc.exe start/stop/query.
    /// </summary>
    public string ServiceName { get; set; } = "";

    /// <summary>
    /// Относительный путь к проекту сервиса.
    /// Сейчас используется как подсказка, а позже пригодится для установки/публикации службы.
    /// </summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Имя папки внутри Deployment:PublishRoot, куда будет опубликован сервис.
    /// Если не задано, Maintenance использует Key или ServiceName.
    /// </summary>
    public string? PublishFolderName { get; set; }

    /// <summary>
    /// Имя exe-файла после dotnet publish.
    /// Если не задано, вычисляется из имени csproj.
    /// </summary>
    public string? ExecutableName { get; set; }

    /// <summary>
    /// Описание Windows Service, которое будет установлено через sc.exe description.
    /// </summary>
    public string? WindowsServiceDescription { get; set; }

    /// <summary>
    /// URL для быстрой HTTP-проверки доступности сервиса.
    /// Для Runtime это status endpoint, для WEB пока достаточно открыть корневую страницу.
    /// </summary>
    public string? HealthUrl { get; set; }
}
