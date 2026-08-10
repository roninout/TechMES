using System.Text.Json;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Contracts.Calc;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Проверяет сохранённое задание по реальному определению алгоритма.
///
/// PostgreSQL store проверяет целостность данных, а этот класс проверяет:
/// существование алгоритма, версию, ключи входов и выходов,
/// обязательные параметры и типы констант.
/// </summary>
internal sealed class CalcJobValidator(CalculationCatalog catalog)
{
    /// <summary>
    /// Проверяет запрос создания или обновления задания.
    /// </summary>
    public CalcJobValidationResult Validate(CalcJobSaveRequest? request, bool isUpdate)
    {
        if (request is null)
            return Invalid("request.missing", "Calculation job request is required.");

        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("job.name-empty", "Calculation job name is required.");

        if (string.IsNullOrWhiteSpace(request.DefinitionCode))
            return Invalid("definition.code-empty", "Calculation definition code is required.");

        if (request.PeriodMs <= 0)
            return Invalid("job.period-invalid", "Calculation period must be greater than zero.");

        if (isUpdate && (!request.ExpectedRevision.HasValue || request.ExpectedRevision.Value <= 0))
            return Invalid("job.revision-required", "ExpectedRevision must be greater than zero when updating a calculation job.");

        if (!isUpdate && request.ExpectedRevision.HasValue)
            return Invalid("job.revision-not-allowed", "ExpectedRevision must be null when creating a calculation job.");

        if (!catalog.TryGet(request.DefinitionCode.Trim(), out var definition) || definition is null)
            return Invalid("definition.not-found", $"Calculation definition '{request.DefinitionCode}' was not found.");

        if (!string.Equals(definition.Version, request.DefinitionVersion?.Trim(), StringComparison.Ordinal))
        {
            return Invalid(
                "definition.version-mismatch",
                $"Calculation definition '{definition.Code}' requires version '{definition.Version}', but version '{request.DefinitionVersion}' was supplied.");
        }

        // Управляемую запись в SCADA добавим отдельным этапом. Пока конфигурация сохраняется только в shadow/read-only режиме.
        if (request.WriteEnabled || (request.Outputs?.Any(output => output is not null && output.WriteEnabled) ?? false))
            return Invalid("calc.write-not-supported", "Calculation result writing is not supported yet.");

        var inputValidation = ValidateInputs(definition, request.Inputs ?? []);
        if (!inputValidation.IsValid)
            return inputValidation;

        return ValidateOutputs(definition, request.Outputs ?? []);
    }

    /// <summary>
    /// Повторно проверяет задание, прочитанное из PostgreSQL.
    ///
    /// Такая проверка необходима, потому что конфигурация могла быть
    /// изменена напрямую в БД либо создана старой версией Runtime.
    /// </summary>
    public CalcJobValidationResult ValidateStored(CalcJobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = new CalcJobSaveRequest
        {
            EquipmentName = job.EquipmentName,
            Name = job.Name,
            Description = job.Description,
            DefinitionCode = job.DefinitionCode,
            DefinitionVersion = job.DefinitionVersion,
            Enabled = job.Enabled,
            PeriodMs = job.PeriodMs,
            WriteEnabled = job.WriteEnabled,
            SortOrder = job.SortOrder,
            ExpectedRevision = job.Revision,

            Inputs = (job.Inputs ?? []).Select(input => new CalcJobInputSaveDto
            {
                ParameterKey = input.ParameterKey,
                SourceType = input.SourceType,
                TagName = input.TagName,
                ConstantValue = input.ConstantValue?.Clone(),
                SourceJobId = input.SourceJobId,
                SourceOutputKey = input.SourceOutputKey,
                MaxAgeSeconds = input.MaxAgeSeconds,
                SortOrder = input.SortOrder
            }).ToList(),

            Outputs = (job.Outputs ?? []).Select(output => new CalcJobOutputSaveDto
            {
                OutputKey = output.OutputKey,
                TagName = output.TagName,
                WriteEnabled = output.WriteEnabled,
                Scale = output.Scale,
                Offset = output.Offset,
                SortOrder = output.SortOrder
            }).ToList()
        };

        return Validate(request, isUpdate: true);
    }

    /// <summary>
    /// Проверяет полный набор входных привязок.
    /// </summary>
    private static CalcJobValidationResult ValidateInputs(ICalculationDefinition definition, IReadOnlyList<CalcJobInputSaveDto> inputs)
    {
        var parameters = definition.Parameters.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var boundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (input is null)
                return Invalid("input.null", "Calculation input binding cannot be null.");

            var key = input.ParameterKey?.Trim() ?? "";

            if (key.Length == 0)
                return Invalid("input.key-empty", "Calculation input parameter key is required.");

            if (!parameters.TryGetValue(key, out var parameter))
                return Invalid("input.unknown", $"Calculation input '{key}' is not supported by definition '{definition.Code}'.");

            if (!boundKeys.Add(key))
                return Invalid("input.duplicate", $"Calculation input '{key}' is specified more than once.");

            var validation = ValidateInputBinding(parameter, input);

            if (!validation.IsValid)
                return validation;
        }

        foreach (var parameter in definition.Parameters)
        {
            // Обязательный параметр можно не сохранять отдельной привязкой, если алгоритм имеет собственное DefaultValue.
            if (parameter.IsRequired && parameter.DefaultValue is null && !boundKeys.Contains(parameter.Key))
            {
                return Invalid("input.required-missing", $"Required calculation input '{parameter.Key}' is missing.");
            }
        }

        return CalcJobValidationResult.Success();
    }

    /// <summary>
    /// Проверяет одну входную привязку в зависимости от SourceType.
    ///
    /// Существование source Job, source output и циклы проверяет
    /// отдельный CalcDependencyGraphValidator.
    /// </summary>
    private static CalcJobValidationResult ValidateInputBinding(CalculationParameterDefinition parameter, CalcJobInputSaveDto input)
    {
        if (input.MaxAgeSeconds.HasValue && input.MaxAgeSeconds.Value <= 0)
            return Invalid("input.max-age-invalid", $"Input '{parameter.Key}' MaxAgeSeconds must be greater than zero.");

        switch (input.SourceType)
        {
            case CalcInputSourceTypeDto.Tag:
                if (string.IsNullOrWhiteSpace(input.TagName))
                    return Invalid("input.tag-empty", $"Tag input '{parameter.Key}' requires TagName.");

                if (input.ConstantValue.HasValue || input.SourceJobId.HasValue || !string.IsNullOrWhiteSpace(input.SourceOutputKey))
                    return Invalid("input.tag-fields-invalid", $"Tag input '{parameter.Key}' can contain only TagName and MaxAgeSeconds.");

                if (parameter.Type is CalculationParameterType.Text or CalculationParameterType.Selection)
                    return Invalid("input.tag-type-unsupported", $"Parameter '{parameter.Key}' of type '{parameter.Type}' cannot currently be read from a SCADA tag.");

                return CalcJobValidationResult.Success();

            case CalcInputSourceTypeDto.Constant:
                if (!input.ConstantValue.HasValue)
                    return Invalid("input.constant-missing", $"Constant input '{parameter.Key}' requires ConstantValue.");

                if (!string.IsNullOrWhiteSpace(input.TagName) || input.SourceJobId.HasValue
                    || !string.IsNullOrWhiteSpace(input.SourceOutputKey) || input.MaxAgeSeconds.HasValue)
                {
                    return Invalid("input.constant-fields-invalid", $"Constant input '{parameter.Key}' can contain only ConstantValue.");
                }

                return ValidateConstant(parameter, input.ConstantValue.Value);

            case CalcInputSourceTypeDto.CalculationOutput:
                if (!input.SourceJobId.HasValue || input.SourceJobId.Value <= 0)
                    return Invalid("input.dependency-job-missing", $"CalculationOutput input '{parameter.Key}' requires SourceJobId.");

                if (string.IsNullOrWhiteSpace(input.SourceOutputKey))
                    return Invalid("input.dependency-output-empty", $"CalculationOutput input '{parameter.Key}' requires SourceOutputKey.");

                if (!string.IsNullOrWhiteSpace(input.TagName) || input.ConstantValue.HasValue)
                    return Invalid("input.dependency-fields-invalid", $"CalculationOutput input '{parameter.Key}' can contain only SourceJobId, SourceOutputKey and MaxAgeSeconds.");

                /*
                 * Все текущие calculation outputs являются double.
                 * Integer разрешаем только если фактический output окажется целым.
                 */
                if (parameter.Type is not CalculationParameterType.Number and not CalculationParameterType.Integer)
                {
                    return Invalid(
                        "input.dependency-type-unsupported",
                        $"CalculationOutput cannot be connected to parameter '{parameter.Key}' of type '{parameter.Type}'.");
                }

                return CalcJobValidationResult.Success();

            default:
                return Invalid("input.source-type-invalid", $"Input '{parameter.Key}' contains an unsupported SourceType.");
        }
    }

    /// <summary>
    /// Проверяет тип, диапазон и Selection-значение константы.
    /// </summary>
    private static CalcJobValidationResult ValidateConstant(CalculationParameterDefinition parameter, JsonElement value)
    {
        switch (parameter.Type)
        {
            case CalculationParameterType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    return Invalid("input.constant-number-invalid", $"Constant input '{parameter.Key}' must be a finite number.");

                return ValidateRange(parameter, number);

            case CalculationParameterType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer))
                    return Invalid("input.constant-integer-invalid", $"Constant input '{parameter.Key}' must be an integer.");

                return ValidateRange(parameter, integer);

            case CalculationParameterType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? CalcJobValidationResult.Success()
                    : Invalid("input.constant-boolean-invalid", $"Constant input '{parameter.Key}' must be true or false.");

            case CalculationParameterType.Text:
                return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    ? CalcJobValidationResult.Success()
                    : Invalid("input.constant-text-invalid", $"Constant input '{parameter.Key}' must contain text.");

            case CalculationParameterType.Selection:
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return Invalid("input.constant-selection-invalid", $"Constant input '{parameter.Key}' must contain a selection value.");

                var selectedValue = value.GetString()!.Trim();
                var allowed = (parameter.Options ?? []).Any(option =>
                    string.Equals(option.Value, selectedValue, StringComparison.OrdinalIgnoreCase));

                return allowed
                    ? CalcJobValidationResult.Success()
                    : Invalid("input.constant-selection-invalid", $"Value '{selectedValue}' is not allowed for input '{parameter.Key}'.");

            default:
                return Invalid("input.constant-type-unsupported", $"Input type '{parameter.Type}' is not supported.");
        }
    }

    /// <summary>
    /// Проверяет числовые ограничения параметра.
    /// </summary>
    private static CalcJobValidationResult ValidateRange(CalculationParameterDefinition parameter, double value)
    {
        if (parameter.Minimum.HasValue && value < parameter.Minimum.Value)
            return Invalid("input.constant-below-minimum", $"Constant input '{parameter.Key}' cannot be less than {parameter.Minimum.Value}.");

        if (parameter.Maximum.HasValue && value > parameter.Maximum.Value)
            return Invalid("input.constant-above-maximum", $"Constant input '{parameter.Key}' cannot be greater than {parameter.Maximum.Value}.");

        return CalcJobValidationResult.Success();
    }

    /// <summary>
    /// Проверяет выходные привязки по описанию алгоритма.
    /// </summary>
    private static CalcJobValidationResult ValidateOutputs(ICalculationDefinition definition, IReadOnlyList<CalcJobOutputSaveDto> outputs)
    {
        var definitions = definition.Outputs.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            if (output is null)
                return Invalid("output.null", "Calculation output binding cannot be null.");

            var key = output.OutputKey?.Trim() ?? "";

            if (key.Length == 0)
                return Invalid("output.key-empty", "Calculation output key is required.");

            if (!definitions.ContainsKey(key))
                return Invalid("output.unknown", $"Calculation output '{key}' is not supported by definition '{definition.Code}'.");

            if (!keys.Add(key))
                return Invalid("output.duplicate", $"Calculation output '{key}' is specified more than once.");

            if (!double.IsFinite(output.Scale) || !double.IsFinite(output.Offset))
                return Invalid("output.transform-invalid", $"Calculation output '{key}' Scale and Offset must be finite numbers.");
        }

        return CalcJobValidationResult.Success();
    }

    private static CalcJobValidationResult Invalid(string code, string message)
    {
        return CalcJobValidationResult.Failure(code, message);
    }
}

/// <summary>
/// Результат проверки конфигурации задания.
/// </summary>
internal sealed record CalcJobValidationResult(bool IsValid, string? ErrorCode, string? ErrorMessage)
{
    public static CalcJobValidationResult Success() => new(true, null, null);

    public static CalcJobValidationResult Failure(string code, string message) =>
        new(false, code, message);
}