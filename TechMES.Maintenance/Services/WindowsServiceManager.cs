using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Управляет Windows Services через sc.exe.
/// Такой подход оставляет проект без дополнительных пакетов и хорошо подходит для первого этапа Maintenance.
/// </summary>
public sealed class WindowsServiceManager(ProcessRunner processRunner)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Читает статус сервиса.
    /// Если сервис не установлен, возвращает мягкий статус Not installed, а не исключение.
    /// </summary>
    public async Task<ServiceCommandResult> QueryAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            "sc.exe",
            ["query", serviceName],
            CommandTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return new ServiceCommandResult
            {
                Success = false,
                Status = "Not installed",
                Details = result.CombinedOutput
            };
        }

        return new ServiceCommandResult
        {
            Success = true,
            Status = ParseState(result.StandardOutput),
            Details = result.StandardOutput
        };
    }

    /// <summary>
    /// Отправляет команду запуска сервиса.
    /// Для реального запуска обычно нужны права администратора.
    /// </summary>
    public Task<ServiceCommandResult> StartAsync(
        string serviceName,
        CancellationToken cancellationToken = default) =>
        ExecuteServiceCommandAsync("start", serviceName, cancellationToken);

    /// <summary>
    /// Отправляет команду остановки сервиса.
    /// Для остановки службы обычно нужны права администратора.
    /// </summary>
    public Task<ServiceCommandResult> StopAsync(
        string serviceName,
        CancellationToken cancellationToken = default) =>
        ExecuteServiceCommandAsync("stop", serviceName, cancellationToken);

    /// <summary>
    /// Перезапускает сервис через stop, небольшую паузу и start.
    /// Это простой вариант для первого этапа; позже можно добавить ожидание конкретных состояний.
    /// </summary>
    public async Task<ServiceCommandResult> RestartAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var stop = await StopAsync(serviceName, cancellationToken);
        await Task.Delay(1000, cancellationToken);
        var start = await StartAsync(serviceName, cancellationToken);

        return new ServiceCommandResult
        {
            Success = stop.Success && start.Success,
            Status = start.Status,
            Details = stop.Details + Environment.NewLine + start.Details
        };
    }

    /// <summary>
    /// Создает Windows Service, если его еще нет, или обновляет binPath/start у существующего сервиса.
    /// Этот метод используется вкладкой Deploy после dotnet publish.
    /// </summary>
    public async Task<ServiceCommandResult> CreateOrUpdateAsync(
        string serviceName,
        string displayName,
        string executablePath,
        string? description,
        bool autoStart,
        CancellationToken cancellationToken = default)
    {
        var query = await QueryAsync(serviceName, cancellationToken);
        var startMode = autoStart ? "auto" : "demand";
        var quotedExecutablePath = $"\"{executablePath}\"";

        var commandResult = query.Status == "Not installed"
            ? await processRunner.RunAsync(
                "sc.exe",
                ["create", serviceName, "binPath=", quotedExecutablePath, "start=", startMode, "DisplayName=", displayName],
                CommandTimeout,
                cancellationToken)
            : await processRunner.RunAsync(
                "sc.exe",
                ["config", serviceName, "binPath=", quotedExecutablePath, "start=", startMode, "DisplayName=", displayName],
                CommandTimeout,
                cancellationToken);

        if (!string.IsNullOrWhiteSpace(description))
        {
            await processRunner.RunAsync(
                "sc.exe",
                ["description", serviceName, description],
                CommandTimeout,
                cancellationToken);
        }

        var updatedStatus = await QueryAsync(serviceName, cancellationToken);

        return new ServiceCommandResult
        {
            Success = commandResult.ExitCode == 0,
            Status = updatedStatus.Status,
            Details = commandResult.CombinedOutput
        };
    }

    /// <summary>
    /// Выполняет sc.exe с указанной командой и возвращает нормализованный результат.
    /// </summary>
    private async Task<ServiceCommandResult> ExecuteServiceCommandAsync(
        string command,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "sc.exe",
            [command, serviceName],
            CommandTimeout,
            cancellationToken);

        var query = await QueryAsync(serviceName, cancellationToken);

        return new ServiceCommandResult
        {
            Success = result.ExitCode == 0,
            Status = query.Status,
            Details = result.CombinedOutput
        };
    }

    /// <summary>
    /// Извлекает человекочитаемое состояние из вывода sc.exe.
    /// Пример строки: STATE              : 4  RUNNING.
    /// </summary>
    private static string ParseState(string output)
    {
        var stateLine = output
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(stateLine))
            return "Unknown";

        var tokens = stateLine
            .Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries);

        return tokens.LastOrDefault() ?? "Unknown";
    }
}
