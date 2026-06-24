namespace TechMES.Maintenance.Models;

/// <summary>
/// Описывает настройки самого обслуживающего приложения.
/// Этот файл не заменяет appsettings WEB/Runtime, а только подсказывает Maintenance,
/// какие сервисы, файлы настроек и папки логов нужно показывать оператору обслуживания.
/// </summary>
public sealed class MaintenanceConfiguration
{
    /// <summary>
    /// Список Windows Services или будущих служб, которыми будет управлять приложение.
    /// Если сервис еще не установлен, строка останется в списке, но получит статус Not installed.
    /// </summary>
    public List<ServiceDefinition> Services { get; set; } = [];

    /// <summary>
    /// Список appsettings-файлов, которые можно открыть и сохранить через Maintenance.
    /// Пути задаются относительно корня репозитория TechMES.
    /// </summary>
    public List<SettingsFileDefinition> SettingsFiles { get; set; } = [];

    /// <summary>
    /// Папки, в которых приложение ищет log-файлы.
    /// На первом этапе это мягкая диагностика: если файлов нет, UI просто покажет пустой список.
    /// </summary>
    public List<string> LogSearchRoots { get; set; } = [];

    /// <summary>
    /// Профиль развертывания WEB/Runtime на сервер.
    /// На первом этапе он хранит только базовые параметры publish и Windows Service install.
    /// </summary>
    public DeploymentOptions Deployment { get; set; } = new();

    /// <summary>
    /// Сетевые параметры серверного режима: WEB-порт, Runtime-порт и firewall-правило.
    /// </summary>
    public ServerOptions Server { get; set; } = new();

    /// <summary>
    /// Настройки очистки старых логов, side-backup файлов и backup-снимков.
    /// </summary>
    public CleanupOptions Cleanup { get; set; } = new();

    /// <summary>
    /// Профиль целевой серверной машины.
    /// </summary>
    public TargetMachineOptions TargetMachine { get; set; } = new();

    /// <summary>
    /// Профиль будущей операторской безопасности и write-режима.
    /// </summary>
    public SecurityOptions Security { get; set; } = new();

    /// <summary>
    /// Возвращает безопасную конфигурацию по умолчанию, если maintenance.settings.json отсутствует
    /// или временно поврежден.
    /// </summary>
    public static MaintenanceConfiguration CreateDefault() => new()
    {
        Services =
        [
            new ServiceDefinition
            {
                Key = "runtime",
                DisplayName = "TechMes Runtime Service",
                ServiceName = "TechMES.Runtime.Service",
                ProjectPath = @"TechMES.Runtime.Service\TechMES.Runtime.Service.csproj",
                HealthUrl = "http://localhost:5101/api/health"
            },
            new ServiceDefinition
            {
                Key = "web",
                DisplayName = "TechMes WEB",
                ServiceName = "TechMES.Web",
                ProjectPath = @"TechMES.Web\TechMES.Web.csproj",
                HealthUrl = "http://localhost:5163/api/health"
            }
        ],
        SettingsFiles =
        [
            new SettingsFileDefinition
            {
                DisplayName = "Runtime appsettings",
                RelativePath = @"TechMES.Runtime.Service\appsettings.json"
            },
            new SettingsFileDefinition
            {
                DisplayName = "WEB appsettings",
                RelativePath = @"TechMES.Web\appsettings.json"
            }
        ],
        LogSearchRoots =
        [
            "_runlogs",
            "TechMES.Runtime.Service",
            "TechMES.Web"
        ],
        Deployment = new DeploymentOptions(),
        Server = new ServerOptions(),
        Cleanup = new CleanupOptions(),
        TargetMachine = new TargetMachineOptions(),
        Security = new SecurityOptions()
    };
}
