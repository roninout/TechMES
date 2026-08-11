using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TechMES.Application.Calc;
using TechMES.Application.Scada;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Единственная точка автоматической записи Calc outputs в SCADA.
///
/// Calc.Service не выбирает целевой тег и не вызывает CtApi.
/// Runtime повторно читает текущий Job из PostgreSQL и сам проверяет:
/// - глобальный CalcWrites.Enabled;
/// - текущий lease;
/// - Enabled и Revision Job;
/// - Job.WriteEnabled;
/// - Output.WriteEnabled;
/// - целевой TagName;
/// - отсутствие нескольких активных writers одного тега.
///
/// Scale и Offset применяются здесь, непосредственно перед TagWrite.
/// </summary>
internal sealed class CalcOutputWriteCoordinator(
    ICalcJobStore jobStore,
    IPlantScadaGateway scadaGateway,
    CalcServiceHeartbeatRegistry heartbeatRegistry,
    CalcWriteDiagnosticsRegistry diagnostics,
    IOptionsMonitor<CalcWriteOptions> options,
    ILogger<CalcOutputWriteCoordinator> logger)
{
    /// <summary>
    /// Обрабатывает только результаты, которые PostgreSQL уже принял
    /// для актуальной Revision.
    ///
    /// Ошибка SCADA write не превращает успешный расчёт в Error:
    /// calculation-state и write-state являются разными состояниями.
    /// </summary>
    public async Task ProcessAcceptedResultsAsync(CalcExecutionResultBatchRequest request, CalcExecutionResultBatchResponse saveResponse, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
            return;

        try
        {
            /*
             * SaveResultsAsync уже проверил текущую Revision и Enabled.
             * Для записи берём только действительно принятые результаты.
             */
            var acceptedResults = saveResponse.Items
                .Where(item => item.Accepted)
                .Select(item => (item.JobId, item.ServiceCycleNumber))
                .ToHashSet();

            if (acceptedResults.Count == 0)
                return;

            /*
             * Снова перечитываем Jobs после сохранения state.
             * Calc.Service snapshot не является источником разрешения записи.
             */
            var currentJobs = await jobStore.GetAllAsync(ct);
            var jobsById = currentJobs.ToDictionary(job => job.Id);

            /*
             * Один SCADA-tag не должен одновременно управляться двумя
             * активными Calc outputs.
             *
             * Даже если такая конфигурация появилась прямым SQL,
             * Runtime откажется писать конфликтующий тег.
             */
            var targetUsage = currentJobs
                .Where(job => job.Enabled && job.WriteEnabled)
                .SelectMany(job => job.Outputs
                    .Where(output => output.WriteEnabled && !string.IsNullOrWhiteSpace(output.TagName))
                    .Select(output => output.TagName!.Trim()))
                .GroupBy(tagName => tagName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var result in request.Items)
            {
                ct.ThrowIfCancellationRequested();

                if (result.Status != CalcJobStateStatusDto.Success)
                    continue;

                if (!acceptedResults.Contains((result.JobId, result.ServiceCycleNumber)))
                    continue;

                if (!options.CurrentValue.Enabled)
                    return;

                if (!heartbeatRegistry.IsLeaseOwner(request.ServiceInstanceId, request.LeaseEpoch, request.LeaseToken))
                {
                    logger.LogWarning(
                        "Calc SCADA write stopped because execution lease is no longer owned. InstanceId={InstanceId}, LeaseEpoch={LeaseEpoch}, LeaseToken={LeaseToken}.",
                        request.ServiceInstanceId, request.LeaseEpoch, request.LeaseToken);

                    return;
                }

                if (!jobsById.TryGetValue(result.JobId, out var job))
                    continue;

                /*
                 * Повторная проверка конфигурации прямо перед write.
                 */
                if (!job.Enabled
                    || !job.WriteEnabled
                    || job.Revision != result.ConfigurationRevision
                    || !string.Equals(job.DefinitionCode, result.DefinitionCode, StringComparison.Ordinal)
                    || !string.Equals(job.DefinitionVersion, result.DefinitionVersion, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var output in job.Outputs.Where(output => output.WriteEnabled))
                {
                    ct.ThrowIfCancellationRequested();

                    if (!options.CurrentValue.Enabled)
                        return;

                    if (!heartbeatRegistry.IsLeaseOwner(request.ServiceInstanceId, request.LeaseEpoch, request.LeaseToken))
                        return;

                    var tagName = output.TagName?.Trim() ?? "";

                    if (tagName.Length == 0)
                    {
                        RecordError(job, output, null, null,
                            "Output is enabled for writing but TagName is empty.");

                        continue;
                    }

                    if (targetUsage.TryGetValue(tagName, out var targetCount) && targetCount > 1)
                    {
                        RecordError(job, output, null, null,
                            $"SCADA tag '{tagName}' is configured as a write target by more than one active Calc output.");

                        continue;
                    }

                    if (!TryReadOutputValue(result.Outputs, output.OutputKey, out var rawValue))
                    {
                        RecordError(job, output, null, null,
                            $"Calculation result does not contain finite output '{output.OutputKey}'.");

                        continue;
                    }

                    /*
                     * Важно:
                     * Scale/Offset НЕ применяются в Calc.Service.
                     * Они принадлежат SCADA binding и применяются Runtime
                     * только сейчас.
                     */
                    var writtenValue = rawValue * output.Scale + output.Offset;

                    if (!double.IsFinite(writtenValue))
                    {
                        RecordError(job, output, rawValue, null,
                            "Output Scale/Offset transformation produced a non-finite value.");

                        continue;
                    }

                    var writeRequest = new ScadaTagWriteRequest
                    {
                        TagName = tagName,
                        Value = writtenValue.ToString("G17", CultureInfo.InvariantCulture),
                        Actor = "TechMES.Calc.Service",
                        Comment = $"Calc Job {job.Id}: {job.Name}; Output={output.OutputKey}; Revision={job.Revision}"
                    };

                    var writeResponse = await scadaGateway.WriteTagAsync(writeRequest, ct);

                    diagnostics.Record(new CalcWriteAttemptDto
                    {
                        AttemptedAtUtc = DateTimeOffset.UtcNow,
                        JobId = job.Id,
                        JobName = job.Name,
                        OutputKey = output.OutputKey,
                        TagName = tagName,
                        RawValue = rawValue,
                        Scale = output.Scale,
                        Offset = output.Offset,
                        WrittenValue = writtenValue,
                        Status = writeResponse.Success
                            ? CalcWriteAttemptStatusDto.Success
                            : CalcWriteAttemptStatusDto.Error,
                        ErrorMessage = writeResponse.Error
                    });

                    if (writeResponse.Success)
                    {
                        logger.LogDebug(
                            "Calc SCADA write succeeded. JobId={JobId}, Output={OutputKey}, Tag={TagName}, Value={Value}.",
                            job.Id, output.OutputKey, tagName, writtenValue);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Calc SCADA write failed. JobId={JobId}, Output={OutputKey}, Tag={TagName}, Error={Error}.",
                            job.Id, output.OutputKey, tagName, writeResponse.Error);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            /*
             * Calculation result уже мог быть сохранён.
             * Поэтому неожиданная ошибка write pipeline не должна
             * заставлять Calc.Service считать state submission неуспешным.
             */
            logger.LogError(ex, "Unexpected Calc SCADA write pipeline error.");
        }
    }

    private void RecordError(CalcJobDto job, CalcJobOutputDto output, double? rawValue, double? writtenValue, string error)
    {
        diagnostics.Record(new CalcWriteAttemptDto
        {
            AttemptedAtUtc = DateTimeOffset.UtcNow,
            JobId = job.Id,
            JobName = job.Name,
            OutputKey = output.OutputKey,
            TagName = output.TagName?.Trim() ?? "",
            RawValue = rawValue,
            Scale = output.Scale,
            Offset = output.Offset,
            WrittenValue = writtenValue,
            Status = CalcWriteAttemptStatusDto.Error,
            ErrorMessage = error
        });

        logger.LogWarning(
            "Calc SCADA write rejected by Runtime. JobId={JobId}, Output={OutputKey}, Error={Error}.",
            job.Id, output.OutputKey, error);
    }

    /// <summary>
    /// Извлекает один конечный числовой output из результата расчёта.
    /// Поиск ключа выполняется без учёта регистра.
    /// </summary>
    private static bool TryReadOutputValue(JsonElement outputs, string outputKey, out double value)
    {
        /*
         * Out-параметр инициализируем сразу.
         * Благодаря этому любой путь выхода из метода корректен
         * с точки зрения definite assignment C#.
         */
        value = default;

        if (outputs.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in outputs.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    outputKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number)
                return false;

            if (!property.Value.TryGetDouble(out var parsedValue))
                return false;

            if (!double.IsFinite(parsedValue))
                return false;

            value = parsedValue;
            return true;
        }

        return false;
    }
}