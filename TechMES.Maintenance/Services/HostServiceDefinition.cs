using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>Добавляет Host в старый профиль Maintenance без сброса остальных служб.</summary>
public static class HostServiceDefinition
{
    public const string ServiceName = "TechMES.Host";

    /// <summary>Создаёт описание службы, используемое Services и Deploy.</summary>
    public static ServiceDefinition Create() => new()
    {
        Key = "host",
        DisplayName = "TechMes Host",
        ServiceName = ServiceName,
        ProjectPath = @"TechMES.Host\TechMES.Host.csproj",
        PublishFolderName = "Host",
        ExecutableName = "TechMES.Host.exe",
        WindowsServiceDescription = "TechMES host availability controller. WEB monitoring and planned shared IPv4 ownership.",
        HealthUrl = "http://127.0.0.1:5116/api/health"
    };

    /// <summary>Повторная инициализация обновляет описание без создания второй строки Host.</summary>
    public static void Ensure(MaintenanceConfiguration configuration)
    {
        var existing = configuration.Services.FirstOrDefault(service =>
            service.Key.Equals("host", StringComparison.OrdinalIgnoreCase)
            || service.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            configuration.Services.Add(Create());
        else
            existing.WindowsServiceDescription = Create().WindowsServiceDescription;
    }
}