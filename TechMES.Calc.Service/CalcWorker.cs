using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service;

/// <summary>
/// Фоновая служба расчётов TechMES.
///
/// На текущем этапе служба только загружает configuration snapshot,
/// проверяет локальную совместимость алгоритмов и отслеживает изменения.
/// Расчёты, чтение тегов и запись результатов ещё не выполняются.
/// </summary>
public sealed class CalcWorker(ILogger<CalcWorker> logger, IRuntimeCalcClient runtimeClient, CalculationCatalog localCatalog, IOptions<CalcRuntimeClientOptions> options) : BackgroundService
{
    private string? _lastSnapshotVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshPeriod = TimeSpan.FromSeconds(options.Value.ConfigurationRefreshSeconds);

        logger.LogInformation(
            "TechMES Calc Service started. Runtime={RuntimeAddress}, ConfigurationRefresh={RefreshSeconds} sec.",
            options.Value.BaseAddress,
            options.Value.ConfigurationRefreshSeconds);

        try
        {
            // Первую конфигурацию загружаем сразу, не ожидая первого периода таймера.
            await RefreshConfigurationAsync(stoppingToken);

            using var timer = new PeriodicTimer(refreshPeriod);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshConfigurationAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальное завершение Windows-службы.
        }

        logger.LogInformation("TechMES Calc Service stopped.");
    }

    /// <summary>
    /// Загружает snapshot и проверяет, что локальная библиотека
    /// Calc.Service содержит те же коды и версии алгоритмов.
    /// </summary>
    private async Task RefreshConfigurationAsync(CancellationToken stoppingToken)
    {
        try
        {
            var snapshot = await runtimeClient.GetConfigurationSnapshotAsync(stoppingToken);

            // Одинаковый Version означает, что конфигурация не менялась.
            if (string.Equals(snapshot.Version, _lastSnapshotVersion, StringComparison.Ordinal))
                return;

            var compatibleJobs = 0;

            foreach (var issue in snapshot.Issues)
            {
                logger.LogWarning(
                    "Calculation job rejected by Runtime. JobId={JobId}, Name={JobName}, Code={ErrorCode}, Error={ErrorMessage}",
                    issue.JobId,
                    issue.JobName,
                    issue.ErrorCode,
                    issue.ErrorMessage);
            }

            foreach (var job in snapshot.Jobs)
            {
                if (!localCatalog.TryGet(job.DefinitionCode, out var definition) || definition is null)
                {
                    logger.LogError(
                        "Calculation job is not supported by local Calc.Service. JobId={JobId}, Definition={DefinitionCode}",
                        job.Id,
                        job.DefinitionCode);

                    continue;
                }

                if (!string.Equals(definition.Version, job.DefinitionVersion, StringComparison.Ordinal))
                {
                    logger.LogError(
                        "Calculation definition version mismatch. JobId={JobId}, Definition={DefinitionCode}, RuntimeVersion={RuntimeVersion}, LocalVersion={LocalVersion}",
                        job.Id,
                        job.DefinitionCode,
                        job.DefinitionVersion,
                        definition.Version);

                    continue;
                }

                compatibleJobs++;
            }

            _lastSnapshotVersion = snapshot.Version;

            logger.LogInformation(
                "Calculation configuration loaded. Version={Version}, Enabled={EnabledCount}, Accepted={AcceptedCount}, LocalCompatible={CompatibleCount}, Issues={IssueCount}",
                snapshot.Version,
                snapshot.EnabledJobCount,
                snapshot.Jobs.Count,
                compatibleJobs,
                snapshot.Issues.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Временная недоступность Runtime не должна останавливать службу.
            logger.LogWarning(ex, "Cannot load calculation configuration from Runtime.Service.");
        }
    }
}