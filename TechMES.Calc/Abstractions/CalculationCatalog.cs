using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Хранит все алгоритмы, доступные в установленной версии TechMES.Calc.
///
/// Каталог будет использоваться Runtime, Calc.Service,
/// WEB-тестером и модульными тестами.
/// </summary>
public sealed class CalculationCatalog
{
    private readonly Dictionary<string, ICalculationDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public CalculationCatalog(IEnumerable<ICalculationDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);

            ValidateDefinition(definition);

            if (!_definitions.TryAdd(definition.Code.Trim(), definition))
            {
                throw new CalculationException(
                    "definition.duplicate",
                    $"Calculation definition '{definition.Code}' is registered more than once.");
            }
        }
    }

    /// <summary>
    /// Возвращает все алгоритмы в порядке категории и названия.
    /// </summary>
    public IReadOnlyList<ICalculationDefinition> GetAll()
    {
        return _definitions.Values
            .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Пытается найти алгоритм по стабильному коду.
    /// </summary>
    public bool TryGet(string code, out ICalculationDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            definition = null;
            return false;
        }

        return _definitions.TryGetValue(code.Trim(), out definition);
    }

    /// <summary>
    /// Возвращает алгоритм либо создаёт понятную ошибку.
    /// </summary>
    public ICalculationDefinition GetRequired(string code)
    {
        if (TryGet(code, out var definition) && definition is not null)
            return definition;

        throw new CalculationException(
            "definition.not-found",
            $"Calculation definition '{code}' was not found.");
    }

    /// <summary>
    /// Проверяет обязательные свойства и уникальность параметров/выходов.
    /// </summary>
    private static void ValidateDefinition(ICalculationDefinition definition)
    {
        ValidateRequiredText(
            definition.Code,
            "definition.code-empty",
            "Calculation definition code cannot be empty.");

        ValidateRequiredText(
            definition.Name,
            "definition.name-empty",
            $"Calculation definition '{definition.Code}' name cannot be empty.");

        ValidateRequiredText(
            definition.Category,
            "definition.category-empty",
            $"Calculation definition '{definition.Code}' category cannot be empty.");

        ValidateRequiredText(
            definition.Version,
            "definition.version-empty",
            $"Calculation definition '{definition.Code}' version cannot be empty.");

        ValidateStableCode(definition.Code);
        ValidateParameters(definition.Code, definition.Parameters);
        ValidateOutputs(definition.Code, definition.Outputs);
    }

    /// <summary>
    /// Стабильный код алгоритма допускает строчные латинские буквы,
    /// цифры, точку и дефис.
    /// </summary>
    private static void ValidateStableCode(string code)
    {
        var normalized = code.Trim();

        var isValid = normalized.Length > 0
            && normalized.All(character =>
                character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character == '.'
                || character == '-');

        if (!isValid)
        {
            throw new CalculationException(
                "definition.code-invalid",
                $"Calculation definition code '{code}' must contain only lowercase letters, numbers, dots and hyphens.");
        }
    }

    /// <summary>
    /// Проверяет метаданные входных параметров.
    /// </summary>
    private static void ValidateParameters(string definitionCode, IReadOnlyList<CalculationParameterDefinition> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            ValidateRequiredText(
                parameter.Key,
                "definition.parameter-key-empty",
                $"Calculation definition '{definitionCode}' contains a parameter with an empty key.");

            ValidateRequiredText(
                parameter.Name,
                "definition.parameter-name-empty",
                $"Calculation parameter '{parameter.Key}' name cannot be empty.");

            if (!keys.Add(parameter.Key))
            {
                throw new CalculationException(
                    "definition.parameter-duplicate",
                    $"Calculation parameter '{parameter.Key}' is defined more than once in '{definitionCode}'.");
            }

            if (parameter.Minimum.HasValue
                && parameter.Maximum.HasValue
                && parameter.Minimum.Value > parameter.Maximum.Value)
            {
                throw new CalculationException(
                    "definition.parameter-range-invalid",
                    $"Calculation parameter '{parameter.Key}' minimum cannot be greater than maximum.");
            }

            if (parameter.Step.HasValue && parameter.Step.Value <= 0)
            {
                throw new CalculationException(
                    "definition.parameter-step-invalid",
                    $"Calculation parameter '{parameter.Key}' step must be greater than zero.");
            }

            if (parameter.Decimals is < 0 or > 15)
            {
                throw new CalculationException(
                    "definition.parameter-decimals-invalid",
                    $"Calculation parameter '{parameter.Key}' decimals must be between 0 and 15.");
            }

            ValidateSelectionOptions(parameter);
        }
    }

    /// <summary>
    /// Проверяет варианты параметра Selection.
    /// </summary>
    private static void ValidateSelectionOptions(CalculationParameterDefinition parameter)
    {
        var options = parameter.Options ?? Array.Empty<CalculationParameterOption>();

        if (parameter.Type == CalculationParameterType.Selection
            && options.Count == 0)
        {
            throw new CalculationException(
                "definition.selection-options-missing",
                $"Selection parameter '{parameter.Key}' must define at least one option.");
        }

        var optionValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            ValidateRequiredText(
                option.Value,
                "definition.selection-value-empty",
                $"Selection parameter '{parameter.Key}' contains an option with an empty value.");

            ValidateRequiredText(
                option.Name,
                "definition.selection-name-empty",
                $"Selection parameter '{parameter.Key}' contains an option with an empty name.");

            if (!optionValues.Add(option.Value))
            {
                throw new CalculationException(
                    "definition.selection-option-duplicate",
                    $"Selection parameter '{parameter.Key}' contains duplicate option '{option.Value}'.");
            }
        }
    }

    /// <summary>
    /// Проверяет метаданные выходных значений.
    /// </summary>
    private static void ValidateOutputs(string definitionCode, IReadOnlyList<CalculationOutputDefinition> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            ValidateRequiredText(
                output.Key,
                "definition.output-key-empty",
                $"Calculation definition '{definitionCode}' contains an output with an empty key.");

            ValidateRequiredText(
                output.Name,
                "definition.output-name-empty",
                $"Calculation output '{output.Key}' name cannot be empty.");

            if (!keys.Add(output.Key))
            {
                throw new CalculationException(
                    "definition.output-duplicate",
                    $"Calculation output '{output.Key}' is defined more than once in '{definitionCode}'.");
            }

            if (output.Decimals is < 0 or > 15)
            {
                throw new CalculationException(
                    "definition.output-decimals-invalid",
                    $"Calculation output '{output.Key}' decimals must be between 0 and 15.");
            }
        }
    }

    /// <summary>
    /// Централизованно проверяет обязательные строки метаданных.
    /// </summary>
    private static void ValidateRequiredText(string? value, string errorCode, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CalculationException(errorCode, errorMessage);
    }
}