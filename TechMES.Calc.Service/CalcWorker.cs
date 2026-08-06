using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;

namespace TechMES.Calc.Service;

/// <summary>
/// Фоновая служба расчётов TechMES.
///
/// На текущем этапе служба:
/// - загружает configuration snapshot;
/// - проверяет локальную совместимость алгоритмов;
/// - один раз читает все уникальные Tag-входы новой конфигурации.
///
/// Расчёты и запись результатов ещё не выполняются.
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
    /// Принимает новый snapshot и выполняет первое контрольное batch-чтение.
    /// </summary>
    private async Task RefreshConfigurationAsync(CancellationToken stoppingToken)
    {
        try
        {
            var snapshot = await runtimeClient.GetConfigurationSnapshotAsync(stoppingToken);

            if (string.Equals(snapshot.Version, _lastSnapshotVersion, StringComparison.Ordinal))
                return;

            var compatibleJobs = ValidateLocalCompatibility(snapshot);
            var tagNames = GetUniqueTagNames(compatibleJobs);

            if (tagNames.Count > 0)
            {
                var batch = await runtimeClient.ReadTagsAsync(tagNames, stoppingToken);
                LogBatchResult(batch);
            }
            else
            {
                logger.LogInformation("Calculation configuration does not contain SCADA Tag inputs.");
            }

            /*
             * Версию принимаем только после успешного HTTP batch-запроса.
             * При сетевой ошибке следующий refresh повторит загрузку и чтение.
             */
            _lastSnapshotVersion = snapshot.Version;

            logger.LogInformation(
                "Calculation configuration accepted. Version={Version}, Enabled={EnabledCount}, RuntimeAccepted={AcceptedCount}, LocalCompatible={CompatibleCount}, Issues={IssueCount}, UniqueTags={UniqueTagCount}",
                snapshot.Version,
                snapshot.EnabledJobCount,
                snapshot.Jobs.Count,
                compatibleJobs.Count,
                snapshot.Issues.Count,
                tagNames.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot load or verify calculation configuration from Runtime.Service.");
        }
    }

    /// <summary>
    /// Оставляет только задания, поддерживаемые локальной библиотекой.
    /// </summary>
    private IReadOnlyList<CalcExecutionJobDto> ValidateLocalCompatibility(CalcConfigurationSnapshotDto snapshot)
    {
        foreach (var issue in snapshot.Issues)
        {
            logger.LogWarning(
                "Calculation job rejected by Runtime. JobId={JobId}, Name={JobName}, Code={ErrorCode}, Error={ErrorMessage}",
                issue.JobId,
                issue.JobName,
                issue.ErrorCode,
                issue.ErrorMessage);
        }

        var compatibleJobs = new List<CalcExecutionJobDto>();

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

            compatibleJobs.Add(job);
        }

        return compatibleJobs;
    }

    /// <summary>
    /// Собирает уникальные теги всех совместимых заданий.
    /// </summary>
    private static IReadOnlyList<string> GetUniqueTagNames(IReadOnlyList<CalcExecutionJobDto> jobs)
    {
        return jobs
            .SelectMany(job => job.Inputs)
            .Where(input => input.SourceType == CalcInputSourceTypeDto.Tag)
            .Select(input => input.TagName?.Trim())
            .Where(tagName => !string.IsNullOrWhiteSpace(tagName))
            .Select(tagName => tagName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Выводит сводку и результаты первого контрольного чтения.
    /// </summary>
    private void LogBatchResult(ScadaTagBatchReadResponse batch)
    {
        logger.LogInformation(
            "SCADA batch read completed. Requested={RequestedCount}, Unique={UniqueCount}, Success={SuccessCount}, Failed={FailureCount}, ReadAtUtc={ReadAtUtc}",
            batch.RequestedCount,
            batch.UniqueCount,
            batch.SuccessCount,
            batch.FailureCount,
            batch.ReadAtUtc);

        foreach (var item in batch.Items)
        {
            if (item.Success)
            {
                logger.LogInformation(
                    "SCADA tag read. Tag={TagName}, Value={Value}, Quality={Quality}, TimestampUtc={TimestampUtc}",
                    item.TagName,
                    item.Value,
                    item.Quality,
                    item.TimestampUtc);
            }
            else
            {
                logger.LogWarning(
                    "SCADA tag read failed. Tag={TagName}, Quality={Quality}, Error={Error}",
                    item.TagName,
                    item.Quality,
                    item.Error);
            }
        }
    }
}