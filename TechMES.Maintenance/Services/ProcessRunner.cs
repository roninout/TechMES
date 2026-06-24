using System.Diagnostics;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Безопасный запуск внешних команд без shell-скриптов.
/// Нужен для sc.exe, потому что управление Windows Services не должно зависеть от PowerShell-политик.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// Запускает процесс, читает stdout/stderr и останавливает его по таймауту.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = await outputTask,
                StandardError = await errorTask
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);

            return new ProcessResult
            {
                ExitCode = -1,
                TimedOut = true,
                StandardError = $"Command timed out: {fileName} {string.Join(" ", arguments)}"
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult
            {
                ExitCode = -1,
                StandardError = ex.Message
            };
        }
    }

    /// <summary>
    /// Best-effort остановка зависшего процесса.
    /// Ошибка kill не должна перекрывать исходную диагностическую ошибку.
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Диагностическая команда уже не удалась; ошибка Kill здесь не несет полезной информации для UI.
        }
    }
}
