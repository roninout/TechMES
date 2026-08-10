using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Service.Execution;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;
using System.Text.Json;

namespace TechMES.Calc.Service;

/// <summary>
/// Фоновая служба shadow-расчётов TechMES.
///
/// Служба периодически обновляет configuration snapshot,
/// запускает задания по их PeriodMs и не записывает результаты в SCADA.
/// </summary>
internal sealed class CalcWorker(ILogger<CalcWorker> logger, IRuntimeCalcClient runtimeClient, CalculationCatalog localCatalog, CalcExecutionEngine executionEngine, CalcServiceIdentity identity,
    CalcServiceLeaseState leaseState, IOptions<CalcRuntimeClientOptions> runtimeOptions, IOptions<CalcExecutionOptions> executionOptions) : BackgroundService
{
    private readonly Dictionary<long, CalcScheduledJobState> _scheduledJobs = [];

    private string? _lastSnapshotVersion;
    private DateTimeOffset _nextConfigurationRefreshUtc = DateTimeOffset.MinValue;
    private long _activeLeaseToken;

    /// <summary>
    /// Выполняет scheduler только пока данный процесс является
    /// текущим владельцем execution lease.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedulerTick = TimeSpan.FromMilliseconds(executionOptions.Value.SchedulerTickMilliseconds);

        logger.LogInformation(
            "TechMES Calc Service started. Runtime={RuntimeAddress}, ConfigurationRefresh={RefreshSeconds} sec, SchedulerTick={SchedulerTick} ms.",
            runtimeOptions.Value.BaseAddress,
            runtimeOptions.Value.ConfigurationRefreshSeconds,
            executionOptions.Value.SchedulerTickMilliseconds);

        try
        {
            using var timer = new PeriodicTimer(schedulerTick);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var lease = leaseState.GetSnapshot(nowUtc);

                if (!lease.IsOwner)
                {
                    DeactivateExecutionLease();

                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                        break;

                    continue;
                }

                ActivateExecutionLease(lease.LeaseToken);

                await RefreshConfigurationIfDueAsync(nowUtc, stoppingToken);

                // Snapshot мог загружаться достаточно долго. Перед самим execution ещё раз убеждаемся, что lease всё ещё действителен.
                if (leaseState.IsCurrentOwner(_activeLeaseToken, DateTimeOffset.UtcNow))
                    await ExecuteDueJobsAsync(DateTimeOffset.UtcNow, _activeLeaseToken, stoppingToken);
                else
                    DeactivateExecutionLease();

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка Windows-службы.
        }

        logger.LogInformation("TechMES Calc Service stopped.");
    }

    /// <summary>
    /// Активирует scheduler для нового lease token.
    ///
    /// Новый token всегда начинает работу со свежего configuration snapshot.
    /// </summary>
    private void ActivateExecutionLease(long leaseToken)
    {
        if (_activeLeaseToken == leaseToken)
            return;

        _activeLeaseToken = leaseToken;
        _scheduledJobs.Clear();
        _lastSnapshotVersion = null;
        _nextConfigurationRefreshUtc = DateTimeOffset.MinValue;

        logger.LogInformation(
            "Calculation scheduler activated. LeaseToken={LeaseToken}.",
            leaseToken);
    }

    /// <summary>
    /// Немедленно прекращает выполнение Jobs после потери lease.
    /// </summary>
    private void DeactivateExecutionLease()
    {
        if (_activeLeaseToken == 0)
            return;

        logger.LogWarning(
            "Calculation scheduler deactivated because execution lease is no longer owned. PreviousLeaseToken={LeaseToken}.",
            _activeLeaseToken);

        _activeLeaseToken = 0;
        _scheduledJobs.Clear();
        _lastSnapshotVersion = null;
        _nextConfigurationRefreshUtc = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Обновляет snapshot с заданной периодичностью.
    ///
    /// При временной ошибке Runtime последняя принятая конфигурация
    /// продолжает выполняться.
    /// </summary>
    private async Task RefreshConfigurationIfDueAsync(DateTimeOffset nowUtc, CancellationToken stoppingToken)
    {
        if (nowUtc < _nextConfigurationRefreshUtc)
            return;

        _nextConfigurationRefreshUtc = nowUtc.AddSeconds(
            runtimeOptions.Value.ConfigurationRefreshSeconds);

        try
        {
            var snapshot = await runtimeClient.GetConfigurationSnapshotAsync(stoppingToken);

            if (string.Equals(snapshot.Version, _lastSnapshotVersion, StringComparison.Ordinal))
                return;

            var compatibleJobs = ValidateLocalCompatibility(snapshot);
            ApplySnapshot(compatibleJobs, nowUtc);

            _lastSnapshotVersion = snapshot.Version;

            logger.LogInformation(
                "Calculation configuration accepted. Version={Version}, Enabled={EnabledCount}, RuntimeAccepted={AcceptedCount}, LocalCompatible={CompatibleCount}, Issues={IssueCount}.",
                snapshot.Version,
                snapshot.EnabledJobCount,
                snapshot.Jobs.Count,
                compatibleJobs.Count,
                snapshot.Issues.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Cannot refresh calculation configuration. The last accepted snapshot remains active.");
        }
    }

    /// <summary>
    /// Оставляет задания, совместимые с локальной TechMES.Calc.
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
                    "Calculation job is not supported locally. JobId={JobId}, Definition={DefinitionCode}.",
                    job.Id,
                    job.DefinitionCode);

                continue;
            }

            if (!string.Equals(definition.Version, job.DefinitionVersion, StringComparison.Ordinal))
            {
                logger.LogError(
                    "Calculation definition version mismatch. JobId={JobId}, Definition={DefinitionCode}, RuntimeVersion={RuntimeVersion}, LocalVersion={LocalVersion}.",
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
    /// Применяет новый snapshot к runtime-расписанию.
    ///
    /// Неизменённые Revision сохраняют существующее время следующего запуска.
    /// Новые и изменённые задания запускаются немедленно.
    /// </summary>
    private void ApplySnapshot(IReadOnlyList<CalcExecutionJobDto> jobs, DateTimeOffset nowUtc)
    {
        var acceptedIds = jobs.Select(job => job.Id).ToHashSet();
        var added = 0;
        var changed = 0;
        var preserved = 0;

        foreach (var job in jobs)
        {
            if (_scheduledJobs.TryGetValue(job.Id, out var existing) && existing.Matches(job))
            {
                existing.Refresh(job);
                preserved++;
                continue;
            }

            if (_scheduledJobs.ContainsKey(job.Id))
                changed++;
            else
                added++;

            _scheduledJobs[job.Id] = new CalcScheduledJobState(job, nowUtc);
        }

        var removedIds = _scheduledJobs.Keys
            .Where(jobId => !acceptedIds.Contains(jobId))
            .ToList();

        foreach (var jobId in removedIds)
            _scheduledJobs.Remove(jobId);

        logger.LogInformation(
            "Calculation schedule updated. Added={Added}, Changed={Changed}, Preserved={Preserved}, Removed={Removed}, Active={Active}.",
            added, changed, preserved, removedIds.Count, _scheduledJobs.Count);
    }

    /// <summary>
    /// Выполняет due Jobs только под конкретным lease token.
    /// </summary>
    private async Task ExecuteDueJobsAsync(DateTimeOffset nowUtc, long leaseToken, CancellationToken stoppingToken)
    {
        var dueStates = _scheduledJobs.Values
            .Where(state => state.IsDue(nowUtc))
            .OrderBy(state => state.Job.SortOrder)
            .ThenBy(state => state.Job.Id)
            .ToList();

        if (dueStates.Count == 0)
            return;

        var requests = dueStates
            .Select(state => new CalcJobExecutionRequest(state.Job, state.NextCycleNumber))
            .ToList();

        try
        {
            var results = await executionEngine.ExecuteAsync(requests, stoppingToken);

            foreach (var result in results)
                LogExecutionResult(result);

            /*
             * Lease мог закончиться во время расчётов.
             * Не отправляем результат как действительный, если ownership уже потерян.
             */
            if (!leaseState.IsCurrentOwner(leaseToken, DateTimeOffset.UtcNow))
            {
                logger.LogWarning(
                    "Calculation results discarded because execution lease expired during the cycle. LeaseToken={LeaseToken}.",
                    leaseToken);

                return;
            }

            await SaveExecutionResultsAsync(results, leaseToken, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Shadow calculation cycle failed before individual job results were produced.");
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

                foreach (var state in dueStates)
                    state.MarkAttemptCompleted(completedAtUtc);
            }
        }
    }

    /// <summary>
    /// Передаёт Runtime результаты вместе с fencing token.
    ///
    /// Runtime повторно проверяет ownership, поэтому локальной
    /// проверки Calc.Service недостаточно для принятия результата.
    /// </summary>
    private async Task SaveExecutionResultsAsync(IReadOnlyList<CalcJobExecutionResult> results, long leaseToken, CancellationToken stoppingToken)
    {
        if (results.Count == 0)
            return;

        try
        {
            var request = new CalcExecutionResultBatchRequest
            {
                ServiceInstanceId = identity.InstanceId,
                LeaseToken = leaseToken,
                SubmittedAtUtc = DateTimeOffset.UtcNow,

                Items = results.Select(result => new CalcExecutionResultItemDto
                {
                    JobId = result.JobId,
                    ConfigurationRevision = result.Revision,
                    ServiceCycleNumber = result.CycleNumber,
                    DefinitionCode = result.DefinitionCode,
                    DefinitionVersion = result.DefinitionVersion,
                    Status = ToStateStatus(result.Status),
                    ReasonCode = result.ReasonCode,
                    ReasonMessage = result.ReasonMessage,
                    StartedAtUtc = result.StartedAtUtc,
                    CompletedAtUtc = result.CompletedAtUtc,
                    DurationMs = result.DurationMs,
                    Inputs = JsonSerializer.SerializeToElement(result.Inputs),
                    Outputs = JsonSerializer.SerializeToElement(result.Outputs)
                }).ToList()
            };

            var response = await runtimeClient.SaveExecutionResultsAsync(request, stoppingToken);

            logger.LogInformation(
                "Calculation states submitted. LeaseToken={LeaseToken}, Requested={Requested}, Accepted={Accepted}, Rejected={Rejected}.",
                leaseToken, response.RequestedCount, response.AcceptedCount, response.RejectedCount);

            foreach (var item in response.Items.Where(item => !item.Accepted))
            {
                logger.LogWarning(
                    "Calculation state rejected. JobId={JobId}, ServiceCycle={ServiceCycle}, Code={ErrorCode}, Error={ErrorMessage}.",
                    item.JobId, item.ServiceCycleNumber, item.ErrorCode, item.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot save calculation states through Runtime.Service.");
        }
    }

    /// <summary>
    /// Преобразует внутренний статус движка в транспортный статус.
    /// </summary>
    private static CalcJobStateStatusDto ToStateStatus(CalcJobExecutionStatus status)
    {
        return status switch
        {
            CalcJobExecutionStatus.Success => CalcJobStateStatusDto.Success,
            CalcJobExecutionStatus.Skipped => CalcJobStateStatusDto.Skipped,
            CalcJobExecutionStatus.Error => CalcJobStateStatusDto.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported calculation status.")
        };
    }

    /// <summary>
    /// Выводит результат одной попытки расчёта.
    /// </summary>
    private void LogExecutionResult(CalcJobExecutionResult result)
    {
        if (result.Status == CalcJobExecutionStatus.Success)
        {
            var outputs = string.Join(
                ", ",
                result.Outputs.Select(output =>
                    $"{output.Key}={output.Value.ToString("G17", CultureInfo.InvariantCulture)}"));

            logger.LogInformation(
                "Shadow calculation succeeded. JobId={JobId}, Name={JobName}, Cycle={CycleNumber}, Revision={Revision}, Definition={DefinitionCode}, DurationMs={DurationMs}, Outputs={Outputs}.",
                result.JobId,
                result.JobName,
                result.CycleNumber,
                result.Revision,
                result.DefinitionCode,
                result.DurationMs,
                outputs);

            return;
        }

        if (result.Status == CalcJobExecutionStatus.Skipped)
        {
            logger.LogWarning(
                "Shadow calculation skipped. JobId={JobId}, Name={JobName}, Cycle={CycleNumber}, Code={ReasonCode}, Reason={ReasonMessage}.",
                result.JobId,
                result.JobName,
                result.CycleNumber,
                result.ReasonCode,
                result.ReasonMessage);

            return;
        }

        logger.LogError(
            "Shadow calculation failed. JobId={JobId}, Name={JobName}, Cycle={CycleNumber}, Code={ReasonCode}, Error={ReasonMessage}.",
            result.JobId,
            result.JobName,
            result.CycleNumber,
            result.ReasonCode,
            result.ReasonMessage);
    }
}