using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;

namespace TechMES.Calc.Service.Execution;

/// <summary>
/// Выполняет один shadow-цикл группы заданий.
///
/// Все уникальные SCADA-теги читаются одним логическим batch-запросом.
/// Результаты только возвращаются и журналируются — запись в SCADA
/// и сохранение calc_job_state пока отсутствуют.
/// </summary>
internal sealed class CalcExecutionEngine(ILogger<CalcExecutionEngine> logger, IRuntimeCalcClient runtimeClient, CalculationCatalog catalog, IOptions<CalcExecutionOptions> options)
{
    private static readonly IReadOnlyDictionary<string, double> EmptyOutputs = new Dictionary<string, double>();

    /// <summary>
    /// Выполняет набор заданий, срок запуска которых наступил.
    /// </summary>
    public async Task<IReadOnlyList<CalcJobExecutionResult>> ExecuteAsync(IReadOnlyList<CalcJobExecutionRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
            return [];

        var tagNames = requests
            .SelectMany(request => request.Job.Inputs)
            .Where(input => input.SourceType == CalcInputSourceTypeDto.Tag)
            .Select(input => input.TagName?.Trim())
            .Where(tagName => !string.IsNullOrWhiteSpace(tagName))
            .Select(tagName => tagName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagValues = new Dictionary<string, ScadaTagBatchReadItem>(StringComparer.OrdinalIgnoreCase);

        if (tagNames.Count > 0)
        {
            var batch = await runtimeClient.ReadTagsAsync(tagNames, ct);

            foreach (var item in batch.Items)
                tagValues[item.TagName] = item;

            logger.LogInformation(
                "Shadow cycle SCADA batch completed. Jobs={JobCount}, UniqueTags={UniqueTagCount}, Success={SuccessCount}, Failed={FailureCount}.",
                requests.Count, batch.UniqueCount, batch.SuccessCount, batch.FailureCount);
        }

        var results = new List<CalcJobExecutionResult>(requests.Count);

        foreach (var request in requests)
            results.Add(ExecuteJob(request, tagValues, ct));

        return results;
    }

    /// <summary>
    /// Выполняет одно задание на уже прочитанном snapshot тегов.
    /// </summary>
    private CalcJobExecutionResult ExecuteJob(CalcJobExecutionRequest request,IReadOnlyDictionary<string, ScadaTagBatchReadItem> tagValues,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var inputValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!catalog.TryGet(request.Job.DefinitionCode, out var definition) || definition is null)
            {
                return Complete(
                    request,
                    CalcJobExecutionStatus.Error,
                    "definition.not-found",
                    $"Calculation definition '{request.Job.DefinitionCode}' is not installed.",
                    startedAtUtc,
                    stopwatch,
                    inputValues,
                    EmptyOutputs);
            }

            if (!string.Equals(definition.Version, request.Job.DefinitionVersion, StringComparison.Ordinal))
            {
                return Complete(
                    request,
                    CalcJobExecutionStatus.Error,
                    "definition.version-mismatch",
                    $"Expected definition version '{request.Job.DefinitionVersion}', but local version is '{definition.Version}'.",
                    startedAtUtc,
                    stopwatch,
                    inputValues,
                    EmptyOutputs);
            }

            if (!TryBuildInputValues(
                    request.Job,
                    definition,
                    tagValues,
                    inputValues,
                    out var reasonCode,
                    out var reasonMessage))
            {
                return Complete(
                    request,
                    CalcJobExecutionStatus.Skipped,
                    reasonCode,
                    reasonMessage,
                    startedAtUtc,
                    stopwatch,
                    inputValues,
                    EmptyOutputs);
            }

            var calculation = definition.Calculate(
                new CalculationParameterSet(inputValues),
                includeTrace: false);

            if (!calculation.IsSuccess)
            {
                return Complete(
                    request,
                    CalcJobExecutionStatus.Error,
                    calculation.ErrorCode ?? "calculation.failed",
                    calculation.ErrorMessage ?? "Calculation failed.",
                    startedAtUtc,
                    stopwatch,
                    inputValues,
                    EmptyOutputs);
            }

            var outputBindings = request.Job.Outputs.ToDictionary(
                output => output.OutputKey,
                StringComparer.OrdinalIgnoreCase);

            var outputValues = new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var output in calculation.Outputs)
            {
                var value = output.Value;

                if (outputBindings.TryGetValue(output.Key, out var binding))
                    value = value * binding.Scale + binding.Offset;

                if (!double.IsFinite(value))
                {
                    return Complete(
                        request,
                        CalcJobExecutionStatus.Error,
                        "output.not-finite",
                        $"Calculation output '{output.Key}' is not a finite number.",
                        startedAtUtc,
                        stopwatch,
                        inputValues,
                        outputValues);
                }

                outputValues[output.Key] = value;
            }

            return Complete(
                request,
                CalcJobExecutionStatus.Success,
                null,
                null,
                startedAtUtc,
                stopwatch,
                inputValues,
                outputValues);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Complete(
                request,
                CalcJobExecutionStatus.Error,
                "calculation.unhandled",
                ex.Message,
                startedAtUtc,
                stopwatch,
                inputValues,
                EmptyOutputs);
        }
    }

    /// <summary>
    /// Собирает значения Tag и Constant для одного задания.
    /// </summary>
    private bool TryBuildInputValues(CalcExecutionJobDto job, ICalculationDefinition definition, IReadOnlyDictionary<string, ScadaTagBatchReadItem> tagValues, Dictionary<string, object?> values, out string? reasonCode, out string? reasonMessage)
    {
        var parameterDefinitions = definition.Parameters.ToDictionary(
            parameter => parameter.Key,
            StringComparer.OrdinalIgnoreCase);

        foreach (var input in job.Inputs.OrderBy(item => item.SortOrder))
        {
            if (!parameterDefinitions.TryGetValue(input.ParameterKey, out var parameter))
            {
                reasonCode = "input.definition-missing";
                reasonMessage = $"Input '{input.ParameterKey}' is not defined by '{definition.Code}'.";
                return false;
            }

            switch (input.SourceType)
            {
                case CalcInputSourceTypeDto.Constant:
                    if (!input.ConstantValue.HasValue
                        || !TryConvertConstant(parameter.Type, input.ConstantValue.Value, out var constantValue))
                    {
                        reasonCode = "input.constant-invalid";
                        reasonMessage = $"Constant input '{input.ParameterKey}' has an invalid value.";
                        return false;
                    }

                    values[input.ParameterKey] = constantValue;
                    break;

                case CalcInputSourceTypeDto.Tag:
                    if (!TryReadTagInput(input, parameter.Type, tagValues,
                            out var tagValue, out reasonCode, out reasonMessage))
                    {
                        return false;
                    }

                    values[input.ParameterKey] = tagValue;
                    break;

                case CalcInputSourceTypeDto.CalculationOutput:
                    reasonCode = "input.dependency-not-supported";
                    reasonMessage = $"CalculationOutput input '{input.ParameterKey}' is not supported yet.";
                    return false;

                default:
                    reasonCode = "input.source-type-invalid";
                    reasonMessage = $"Input '{input.ParameterKey}' has an unsupported source type.";
                    return false;
            }
        }

        reasonCode = null;
        reasonMessage = null;
        return true;
    }

    /// <summary>
    /// Проверяет качество, возраст и значение одного Tag-входа.
    /// </summary>
    private bool TryReadTagInput(CalcExecutionInputDto input, CalculationParameterType parameterType, IReadOnlyDictionary<string, ScadaTagBatchReadItem> tagValues, out object? value, out string? reasonCode, out string? reasonMessage)
    {
        var tagName = input.TagName?.Trim() ?? "";

        if (tagName.Length == 0 || !tagValues.TryGetValue(tagName, out var item))
        {
            value = null;
            reasonCode = "input.tag-read-missing";
            reasonMessage = $"SCADA tag result for input '{input.ParameterKey}' was not returned.";
            return false;
        }

        if (!item.Success)
        {
            value = null;
            reasonCode = "input.tag-read-failed";
            reasonMessage = $"SCADA tag '{tagName}' failed: {item.Error}";
            return false;
        }

        if (item.Quality is ScadaTagQuality.Bad or ScadaTagQuality.Uncertain)
        {
            value = null;
            reasonCode = "input.tag-quality-bad";
            reasonMessage = $"SCADA tag '{tagName}' has quality '{item.Quality}'.";
            return false;
        }

        if (item.Quality == ScadaTagQuality.Unknown && !options.Value.AcceptUnknownQuality)
        {
            value = null;
            reasonCode = "input.tag-quality-unknown";
            reasonMessage = $"SCADA tag '{tagName}' has unknown quality.";
            return false;
        }

        var maxAgeSeconds = input.MaxAgeSeconds ?? options.Value.DefaultMaxAgeSeconds;

        if (item.TimestampUtc == default)
        {
            value = null;
            reasonCode = "input.tag-timestamp-missing";
            reasonMessage = $"SCADA tag '{tagName}' does not contain a timestamp.";
            return false;
        }

        var age = DateTimeOffset.UtcNow - item.TimestampUtc;

        if (age > TimeSpan.FromSeconds(maxAgeSeconds))
        {
            value = null;
            reasonCode = "input.tag-stale";
            reasonMessage = $"SCADA tag '{tagName}' is stale. Age: {age.TotalSeconds:F1} sec.";
            return false;
        }

        if (!TryConvertTagValue(parameterType, item.Value, out value))
        {
            reasonCode = "input.tag-value-invalid";
            reasonMessage = $"SCADA tag '{tagName}' value '{item.Value}' cannot be converted to {parameterType}.";
            return false;
        }

        reasonCode = null;
        reasonMessage = null;
        return true;
    }

    /// <summary>
    /// Преобразует JSON-константу в тип параметра расчёта.
    /// </summary>
    private static bool TryConvertConstant(CalculationParameterType parameterType, JsonElement element, out object? value)
    {
        switch (parameterType)
        {
            case CalculationParameterType.Number when element.TryGetDouble(out var number) && double.IsFinite(number):
                value = number;
                return true;

            case CalculationParameterType.Integer when element.TryGetInt32(out var integer):
                value = integer;
                return true;

            case CalculationParameterType.Boolean when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean();
                return true;

            case CalculationParameterType.Selection:
            case CalculationParameterType.Text:
                if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
                {
                    value = element.GetString()!.Trim();
                    return true;
                }

                break;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Преобразует строковое SCADA-значение в тип параметра.
    /// </summary>
    private static bool TryConvertTagValue(CalculationParameterType parameterType, string? source, out object? value)
    {
        var text = (source ?? "").Trim();

        switch (parameterType)
        {
            case CalculationParameterType.Number when TryParseDouble(text, out var number):
                value = number;
                return true;

            case CalculationParameterType.Integer when TryParseInteger(text, out var integer):
                value = integer;
                return true;

            case CalculationParameterType.Boolean when TryParseBoolean(text, out var boolean):
                value = boolean;
                return true;

            case CalculationParameterType.Selection:
            case CalculationParameterType.Text:
                if (text.Length > 0)
                {
                    value = text;
                    return true;
                }

                break;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Разбирает число с точкой, системным разделителем или запятой.
    /// </summary>
    private static bool TryParseDouble(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }

        if (text.Contains(',') && !text.Contains('.')
            && double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Разбирает целое значение без потери дробной части.
    /// </summary>
    private static bool TryParseInteger(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        if (TryParseDouble(text, out var number)
            && number is >= int.MinValue and <= int.MaxValue
            && Math.Truncate(number) == number)
        {
            value = (int)number;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Разбирает типичные SCADA-представления логического значения.
    /// </summary>
    private static bool TryParseBoolean(string text, out bool value)
    {
        if (bool.TryParse(text, out value))
            return true;

        if (text is "1" || text.Equals("On", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (text is "0" || text.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Завершает формирование результата и фиксирует длительность.
    /// </summary>
    private static CalcJobExecutionResult Complete(CalcJobExecutionRequest request, CalcJobExecutionStatus status, string? reasonCode, string? reasonMessage, DateTimeOffset startedAtUtc, Stopwatch stopwatch, IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, double> outputs)
    {
        stopwatch.Stop();

        return new CalcJobExecutionResult
        {
            JobId = request.Job.Id,
            Revision = request.Job.Revision,
            CycleNumber = request.CycleNumber,
            JobName = request.Job.Name,
            DefinitionCode = request.Job.DefinitionCode,
            DefinitionVersion = request.Job.DefinitionVersion,
            Status = status,
            ReasonCode = reasonCode,
            ReasonMessage = reasonMessage,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = Math.Max(0, (long)stopwatch.Elapsed.TotalMilliseconds),
            Inputs = inputs,
            Outputs = outputs
        };
    }
}