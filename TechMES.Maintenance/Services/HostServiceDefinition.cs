using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>Добавляет Host в старый профиль Maintenance без сброса остальных служб.</summary>
public static class HostServiceDefinition
{
    public const string ServiceName = "TechMES.Host";

    /// <summary>Создаёт описание службы, используемое Services и Deploy.</summary>
    public static ServiceDefinition Create() => new()
    {
        Key = "host", DisplayName = "TechMes Host", ServiceName = ServiceName,
        ProjectPath = @"TechMES.Host\TechMES.Host.csproj", PublishFolderName = "Host",
        ExecutableName = "TechMES.Host.exe",
        WindowsServiceDescription = "TechMES host availability controller. Observer mode; shared IP management is disabled.",
        HealthUrl = "http://127.0.0.1:5116/api/health"
    };

    /// <summary>Повторная инициализация не создаёт вторую строку Host.</summary>
    public static void Ensure(MaintenanceConfiguration configuration)
    {
        if (!configuration.Services.Any(service => service.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || service.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)))
            configuration.Services.Add(Create());
    }
}
