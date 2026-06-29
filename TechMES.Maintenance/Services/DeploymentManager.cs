using System.IO;
using System.Text;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Выполняет серверное развертывание TechMES.
/// Разделяем deploy-логику и UI, чтобы позже безболезненно добавить HTTPS, firewall и установку PostgreSQL.
/// </summary>
public sealed class DeploymentManager(
    DirectoryInfo repositoryRoot,
    ProcessRunner processRunner,
    WindowsServiceManager serviceManager)
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Вычисляет папку публикации конкретного сервиса.
    /// </summary>
    public string GetPublishDirectory(
        ServiceDefinition service,
        DeploymentOptions options)
    {
        var folderName = service.PublishFolderName;

        if (string.IsNullOrWhiteSpace(folderName))
            folderName = !string.IsNullOrWhiteSpace(service.Key) ? service.Key : service.ServiceName;

        return Path.Combine(options.PublishRoot, folderName);
    }

    /// <summary>
    /// Вычисляет полный путь к exe, который должен стать binPath Windows Service.
    /// </summary>
    public string GetExecutablePath(
        ServiceDefinition service,
        DeploymentOptions options)
    {
        var executableName = service.ExecutableName;

        if (string.IsNullOrWhiteSpace(executableName))
        {
            var projectFileName = Path.GetFileNameWithoutExtension(service.ProjectPath ?? service.ServiceName);
            executableName = projectFileName + ".exe";
        }

        return Path.Combine(GetPublishDirectory(service, options), executableName);
    }

    /// <summary>
    /// Публикует проект сервиса в папку развертывания через dotnet publish.
    /// Команда выполняется без shell, поэтому аргументы не склеиваются строками.
    /// </summary>
    public async Task<ProcessResult> PublishAsync(
        ServiceDefinition service,
        DeploymentOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service.ProjectPath))
        {
            return new ProcessResult
            {
                ExitCode = -1,
                StandardError = "ProjectPath is empty."
            };
        }

        var projectPath = Path.Combine(repositoryRoot.FullName, service.ProjectPath);
        var publishDirectory = GetPublishDirectory(service, options);

        Directory.CreateDirectory(publishDirectory);

        var arguments = new List<string>
        {
            "publish",
            projectPath,
            "-c",
            options.Configuration,
            "-o",
            publishDirectory,
            "-p:UseAppHost=true"
        };

        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
        {
            arguments.Add("-r");
            arguments.Add(options.RuntimeIdentifier);
            arguments.Add("--self-contained");
            arguments.Add(options.SelfContained ? "true" : "false");
        }

        var publishedAppsettingsPath = Path.Combine(publishDirectory, "appsettings.json");
        var preservedPublishedAppsettings = File.Exists(publishedAppsettingsPath)
            ? File.ReadAllText(publishedAppsettingsPath, Encoding.UTF8)
            : null;

        var result = await processRunner.RunAsync(
            "dotnet",
            arguments,
            PublishTimeout,
            cancellationToken);

        if (preservedPublishedAppsettings is not null)
        {
            File.WriteAllText(publishedAppsettingsPath, preservedPublishedAppsettings, Encoding.UTF8);
            var standardOutput = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? $"Preserved published appsettings: {publishedAppsettingsPath}"
                : result.StandardOutput + Environment.NewLine + $"Preserved published appsettings: {publishedAppsettingsPath}";

            return new ProcessResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = standardOutput,
                StandardError = result.StandardError,
                TimedOut = result.TimedOut
            };
        }

        return result;
    }

    /// <summary>
    /// Устанавливает или обновляет Windows Service после публикации.
    /// Если exe еще не существует, возвращает понятную ошибку вместо вызова sc.exe.
    /// </summary>
    public async Task<ServiceCommandResult> InstallOrUpdateServiceAsync(
        ServiceDefinition service,
        DeploymentOptions options,
        CancellationToken cancellationToken = default)
    {
        var executablePath = GetExecutablePath(service, options);

        if (!File.Exists(executablePath))
        {
            return new ServiceCommandResult
            {
                Success = false,
                Status = "Publish required",
                Details = $"Executable not found: {executablePath}"
            };
        }

        return await serviceManager.CreateOrUpdateAsync(
            service.ServiceName,
            service.DisplayName,
            executablePath,
            service.WindowsServiceDescription,
            options.AutoStart,
            cancellationToken);
    }
}
