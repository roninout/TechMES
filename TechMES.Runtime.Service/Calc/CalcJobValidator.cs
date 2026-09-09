using System.Text.Json;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Formula;
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

        var inputValidation = ValidateInputs(definition, request.Inputs ?? []);

        if (!inputValidation.IsValid)
            return inputValidation;

        var outputValidation = ValidateOutputs(definition, request.Outputs ?? []);

        if (!outputValidation.IsValid)
            return outputValidation;

        return ValidateSpecializedConfiguration(definition, request);
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
    ///
    /// Write-enabled output обязан иметь целевой SCADA tag.
    /// Само глобальное разрешение CalcWrites здесь не проверяется:
    /// конфигурацию Job можно подготовить заранее, пока master switch выключен.
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
                return Invalid("output.unknown",
                    $"Calculation output '{key}' is not supported by definition '{definition.Code}'.");

            if (!keys.Add(key))
                return Invalid("output.duplicate",
                    $"Calculation output '{key}' is specified more than once.");

            if (!double.IsFinite(output.Scale) || !double.IsFinite(output.Offset))
                return Invalid("output.transform-invalid",
                    $"Calculation output '{key}' Scale and Offset must be finite numbers.");

            if (output.WriteEnabled && string.IsNullOrWhiteSpace(output.TagName))
                return Invalid("output.write-tag-empty",
                    $"Calculation output '{key}' is enabled for writing but Target Tag is empty.");
        }

        return CalcJobValidationResult.Success();
    }

    /// <summary>
    /// Дополнительные правила специализированных расчётов.
    /// Formula проверяется отдельно по своему DefinitionCode.
    /// Существующие правила Tank сохраняются.
    /// </summary>
    private static CalcJobValidationResult ValidateSpecializedConfiguration(ICalculationDefinition definition, CalcJobSaveRequest request)
    {
        if (string.Equals(definition.Code, FormulaCalculationDefinition.DefinitionCode, StringComparison.OrdinalIgnoreCase))
            return ValidateFormulaConfiguration(request);

        if (!string.Equals(definition.Category, "Tanks", StringComparison.OrdinalIgnoreCase))
            return CalcJobValidationResult.Success();

        var inputs = request.Inputs ?? [];
        var outputs = request.Outputs ?? [];

        if (request.Enabled)
        {
            var levelRaw = inputs.FirstOrDefault(input => string.Equals(input.ParameterKey, "levelRaw", StringComparison.OrdinalIgnoreCase));

            if (levelRaw is null || levelRaw.SourceType != CalcInputSourceTypeDto.Tag || string.IsNullOrWhiteSpace(levelRaw.TagName))
            {
                return Invalid("tank.level-binding-required", "Enabled Tank Job requires Level raw to be linked to the Level.R SCADA tag.");
            }

            var densityHmi = inputs.FirstOrDefault(input => string.Equals(input.ParameterKey, "densityHmi", StringComparison.OrdinalIgnoreCase));

            if (densityHmi is null || densityHmi.SourceType != CalcInputSourceTypeDto.Tag || string.IsNullOrWhiteSpace(densityHmi.TagName))
            {
                return Invalid("tank.density-binding-required", "Enabled Tank Job requires Density HMI to be linked to a SCADA tag.");
            }
        }

        /*
         * Для пользователя существует только один switch:
         * Allow output writes = Job.WriteEnabled.
         *
         * Внутренний Output.WriteEnabled оставляем только ради существующего
         * общего Runtime safety pipeline, но для Tank все четыре выхода
         * должны включаться одновременно.
         */
        if (request.WriteEnabled)
        {
            string[] requiredOutputs = ["hMax", "levelMm", "volume", "mass"];

            foreach (var outputKey in requiredOutputs)
            {
                var output = outputs.FirstOrDefault(item => string.Equals(item.OutputKey, outputKey, StringComparison.OrdinalIgnoreCase));

                if (output is null)
                {
                    return Invalid("tank.output-binding-missing", $"Tank output '{outputKey}' is required when output writes are enabled.");
                }

                if (!output.WriteEnabled)
                {
                    return Invalid("tank.output-write-inconsistent", $"Tank output '{outputKey}' must be enabled together with the global Allow output writes option.");
                }

                if (string.IsNullOrWhiteSpace(output.TagName))
                {
                    return Invalid("tank.output-tag-missing", $"Tank output '{outputKey}' does not have a target SCADA tag.");
                }
            }
        }

        return CalcJobValidationResult.Success();
    }

    /// <summary>
    /// Проверяет связанные настройки Formula после общей проверки типов и ключей.
    /// Выключенный Job допускает незавершённые входные привязки.
    /// </summary>
    private static CalcJobValidationResult ValidateFormulaConfiguration(CalcJobSaveRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.EquipmentName))
            return Invalid("formula.equipment-not-allowed", "Formula Job is independent from SCADA Equipment and EquipmentName must be empty.");

        var inputs = request.Inputs ?? [];
        var outputs = request.Outputs ?? [];
        var expressionInput = inputs.FirstOrDefault(input => FormulaKeyEquals(input.ParameterKey, FormulaCalculationDefinition.ExpressionKey));

        if (expressionInput is null || expressionInput.SourceType != CalcInputSourceTypeDto.Constant || !TryGetFormulaText(expressionInput.ConstantValue, out var expression))
            return Invalid("formula.expression-constant-required", "Formula expression must be configured as a non-empty constant text value.");

        IReadOnlyList<string> referencedVariables;

        try
        {
            referencedVariables = FormulaExpressionEngine.Validate(expression);
        }
        catch (CalculationException exception)
        {
            return Invalid(exception.Code, exception.Message);
        }

        if (referencedVariables.Count == 0)
            return Invalid("formula.variable-required", "Formula must reference at least one process variable [a]..[z].");

        // Допускаем пропуски букв и входы, которые сейчас не используются в формуле. Для включённого Job по-прежнему обязательны привязки всех используемых переменных.
        var variableInputs = inputs.Where(input => IsFormulaVariable(input.ParameterKey)).ToDictionary(input => input.ParameterKey.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var variable in referencedVariables)
        {
            if (request.Enabled && !variableInputs.ContainsKey(variable))
                return Invalid("formula.variable-binding-required", $"Enabled Formula Job requires a source binding for variable [{variable}].");
        }

        foreach (var input in variableInputs)
        {
            if (input.Value.SourceType != CalcInputSourceTypeDto.Tag)
                return Invalid("formula.variable-tag-required", $"Formula variable [{input.Key}] must be linked to a numeric SCADA tag.");
        }

        var minimumInput = inputs.FirstOrDefault(input => FormulaKeyEquals(input.ParameterKey, FormulaCalculationDefinition.OutputMinimumKey));
        var maximumInput = inputs.FirstOrDefault(input => FormulaKeyEquals(input.ParameterKey, FormulaCalculationDefinition.OutputMaximumKey));

        if ((minimumInput is null) != (maximumInput is null))
            return Invalid("formula.output-range-incomplete", "Formula output minimum and maximum must be configured together.");

        if (minimumInput is not null && maximumInput is not null)
        {
            if (!TryGetFormulaConstantDouble(minimumInput, out var minimum) || !TryGetFormulaConstantDouble(maximumInput, out var maximum))
                return Invalid("formula.output-range-constant-required", "Formula output minimum and maximum must be finite constant values.");

            if (minimum >= maximum)
                return Invalid("formula.output-range-invalid", "Formula output minimum must be less than output maximum.");
        }

        var resultOutput = outputs.FirstOrDefault(output => FormulaKeyEquals(output.OutputKey, FormulaCalculationDefinition.ResultOutputKey));

        // Границы относятся к результату формулы. Дополнительное преобразование после проверки диапазона могло бы вывести записанное значение за его пределы.
        if (resultOutput is not null && (resultOutput.Scale != 1d || resultOutput.Offset != 0d))
            return Invalid("formula.output-transform-not-allowed", "Formula output requires Scale = 1 and Offset = 0. Apply transformations inside the expression.");

        if (request.WriteEnabled)
        {
            if (!request.Enabled)
                return Invalid("formula.write-requires-enabled-job", "Formula Job must be enabled before output writes can be enabled.");

            if (minimumInput is null || maximumInput is null)
                return Invalid("formula.write-range-required", "Formula output minimum and maximum are required before output writes can be enabled.");

            if (resultOutput is null || !resultOutput.WriteEnabled || string.IsNullOrWhiteSpace(resultOutput.TagName))
                return Invalid("formula.output-binding-required", "Formula result output must have a target SCADA tag and be enabled together with Allow output writes.");
        }
        else if (resultOutput?.WriteEnabled == true)
        {
            return Invalid("formula.output-write-inconsistent", "Formula result output cannot be write-enabled while Allow output writes is disabled.");
        }

        return CalcJobValidationResult.Success();
    }

    private static bool FormulaKeyEquals(string? key, string expected)
    {
        return string.Equals(key?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFormulaVariable(string? key)
    {
        var normalized = key?.Trim().ToLowerInvariant();
        return normalized is { Length: 1 } && normalized[0] is >= 'a' and <= 'z';
    }

    private static bool TryGetFormulaText(JsonElement? value, out string result)
    {
        result = value is { ValueKind: JsonValueKind.String } text ? text.GetString()?.Trim() ?? "" : "";
        return result.Length > 0;
    }

    private static bool TryGetFormulaConstantDouble(CalcJobInputSaveDto input, out double result)
    {
        result = default;
        return input.SourceType == CalcInputSourceTypeDto.Constant && input.ConstantValue is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out result) && double.IsFinite(result);
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