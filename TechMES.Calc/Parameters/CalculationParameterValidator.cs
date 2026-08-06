using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Parameters;

/// <summary>
/// Проверяет фактические значения параметров по описанию алгоритма.
///
/// Класс находится внутри расчётного ядра, чтобы Calc.Service,
/// Runtime и WEB использовали одинаковые правила валидации.
/// </summary>
internal static class CalculationParameterValidator
{
    /// <summary>
    /// Добавляет значения по умолчанию, отклоняет неизвестные параметры
    /// и проверяет типы, диапазоны и варианты Selection.
    /// </summary>
    public static CalculationParameterSet CreateValidatedSet(IReadOnlyList<CalculationParameterDefinition> definitions, CalculationParameterSet suppliedParameters)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(suppliedParameters);

        var definitionsByKey = new Dictionary<string, CalculationParameterDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            if (!definitionsByKey.TryAdd(definition.Key, definition))
            {
                throw new CalculationException("definition.parameter-duplicate", $"Calculation parameter definition '{definition.Key}' is duplicated.");
            }
        }

        // Неизвестные параметры обычно означают ошибку в имени поля
        // или несовместимость конфигурации с версией алгоритма.
        foreach (var suppliedKey in suppliedParameters.Values.Keys)
        {
            if (!definitionsByKey.ContainsKey(suppliedKey))
            {
                throw new CalculationException("parameter.unknown", $"Calculation parameter '{suppliedKey}' is not supported.");
            }
        }

        var effectiveValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in suppliedParameters.Values)
            effectiveValues[item.Key] = item.Value;

        // Значение по умолчанию используется, когда параметр отсутствует
        // либо явно содержит null.
        foreach (var definition in definitions)
        {
            var hasValue = effectiveValues.TryGetValue(definition.Key, out var suppliedValue) && suppliedValue is not null;

            if (!hasValue && definition.DefaultValue is not null)
                effectiveValues[definition.Key] = definition.DefaultValue;
        }

        var effectiveParameters = new CalculationParameterSet(effectiveValues);

        foreach (var definition in definitions.OrderBy(x => x.Order))
            ValidateValue(definition, effectiveParameters);

        return effectiveParameters;
    }

    /// <summary>
    /// Проверяет одно значение в зависимости от типа параметра.
    /// </summary>
    private static void ValidateValue(CalculationParameterDefinition definition, CalculationParameterSet parameters)
    {
        var hasValue = parameters.TryGetValue(definition.Key, out var rawValue) && rawValue is not null;

        if (!hasValue)
        {
            if (definition.IsRequired)
            {
                throw new CalculationException(
                    "parameter.missing",
                    $"Required calculation parameter '{definition.Key}' is missing.");
            }

            return;
        }

        switch (definition.Type)
        {
            case CalculationParameterType.Number:
                {
                    var value = parameters.GetRequiredDouble(definition.Key);
                    ValidateRange(definition, value);
                    break;
                }

            case CalculationParameterType.Integer:
                {
                    var value = parameters.GetRequiredInt(definition.Key);
                    ValidateRange(definition, value);
                    break;
                }

            case CalculationParameterType.Boolean:
                parameters.GetRequiredBoolean(definition.Key);
                break;

            case CalculationParameterType.Text:
                parameters.GetRequiredString(definition.Key);
                break;

            case CalculationParameterType.Selection:
                ValidateSelection(definition, parameters);
                break;

            default:
                throw new CalculationException(
                    "definition.parameter-type-unsupported",
                    $"Calculation parameter type '{definition.Type}' is not supported.");
        }
    }

    /// <summary>
    /// Проверяет минимальную и максимальную допустимую границу.
    /// </summary>
    private static void ValidateRange(CalculationParameterDefinition definition, double value)
    {
        if (definition.Minimum.HasValue && value < definition.Minimum.Value)
        {
            throw new CalculationException(
                "parameter.below-minimum",
                $"Calculation parameter '{definition.Key}' cannot be less than {definition.Minimum.Value}.");
        }

        if (definition.Maximum.HasValue && value > definition.Maximum.Value)
        {
            throw new CalculationException(
                "parameter.above-maximum",
                $"Calculation parameter '{definition.Key}' cannot be greater than {definition.Maximum.Value}.");
        }
    }

    /// <summary>
    /// Проверяет, что выбранное строковое значение присутствует
    /// среди вариантов, объявленных алгоритмом.
    /// </summary>
    private static void ValidateSelection(CalculationParameterDefinition definition, CalculationParameterSet parameters)
    {
        var value = parameters.GetRequiredString(definition.Key);
        var options = definition.Options ?? Array.Empty<CalculationParameterOption>();

        if (options.Count == 0)
        {
            throw new CalculationException(
                "definition.selection-options-missing",
                $"Selection parameter '{definition.Key}' does not define any options.");
        }

        var isAllowed = options.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            throw new CalculationException(
                "parameter.selection-invalid",
                $"Value '{value}' is not allowed for calculation parameter '{definition.Key}'.");
        }
    }
}