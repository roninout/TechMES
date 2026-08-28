using System.Text.Json;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Contracts.Calc;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Преобразует внутренние модели TechMES.Calc
/// в транспортные модели TechMES.Contracts.
///
/// Такое разделение не позволяет WEB и Maintenance зависеть
/// от внутреннего устройства расчётного ядра.
/// </summary>
internal static class CalcContractMapper
{
    /// <summary>
    /// Преобразует полное описание алгоритма в DTO.
    /// </summary>
    public static CalcDefinitionDto ToDefinitionDto(ICalculationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CalcDefinitionDto
        {
            Code = definition.Code,
            Name = definition.Name,
            Category = definition.Category,
            Version = definition.Version,

            Parameters = definition.Parameters
                .OrderBy(parameter => parameter.Order)
                .Select(ToParameterDefinitionDto)
                .ToList(),

            Outputs = definition.Outputs
                .OrderBy(output => output.Order)
                .Select(output => new CalcOutputDefinitionDto
                {
                    Key = output.Key,
                    Name = output.Name,
                    Unit = output.Unit,
                    Decimals = output.Decimals,
                    Order = output.Order,
                    Description = output.Description
                })
                .ToList()
        };
    }

    /// <summary>
    /// Преобразует JSON-параметры запроса в CalculationParameterSet.
    ///
    /// Преобразование выполняется в Runtime, чтобы TechMES.Calc
    /// не получал зависимости от HTTP и System.Text.Json.
    /// </summary>
    public static CalculationParameterSet ToParameterSet(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (parameters is null)
            return new CalculationParameterSet(values);

        foreach (var item in parameters)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                throw new CalculationException(
                    "parameter.key-empty",
                    "Calculation parameter key cannot be empty.");
            }

            var key = item.Key.Trim();
            var value = ConvertJsonValue(key, item.Value);

            // JSON допускает ключи, отличающиеся только регистром.
            // Для расчётного ядра такие параметры считаются дубликатами.
            if (!values.TryAdd(key, value))
            {
                throw new CalculationException(
                    "parameter.duplicate",
                    $"Calculation parameter '{key}' is specified more than once.");
            }
        }

        return new CalculationParameterSet(values);
    }

    /// <summary>
    /// Преобразует внутренний результат расчёта в API response.
    /// </summary>
    public static CalcTestResponse ToTestResponse(ICalculationDefinition definition, CalculationResult result)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(result);

        return new CalcTestResponse
        {
            DefinitionCode = definition.Code,
            DefinitionVersion = definition.Version,
            IsSuccess = result.IsSuccess,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,

            Outputs = result.Outputs
                .Select(output => new CalcOutputDto
                {
                    Key = output.Key,
                    Name = output.Name,
                    Value = output.Value,
                    Unit = output.Unit
                })
                .ToList(),

            Messages = result.Messages
                .Select(message => new CalcMessageDto
                {
                    Code = message.Code,
                    Message = message.Message,
                    Severity = ToMessageSeverityDto(message.Severity)
                })
                .ToList(),

            Trace = result.Trace
                .Select(item => new CalcTraceItemDto
                {
                    Key = item.Key,
                    Name = item.Name,
                    Value = item.Value,
                    Unit = item.Unit
                })
                .ToList()
        };
    }

    /// <summary>
    /// Создаёт неуспешный ответ, если параметры запроса
    /// невозможно преобразовать до запуска алгоритма.
    /// </summary>
    public static CalcTestResponse ToFailureResponse(ICalculationDefinition definition, string errorCode, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CalcTestResponse
        {
            DefinitionCode = definition.Code,
            DefinitionVersion = definition.Version,
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Преобразует внутреннее описание одного Calculation Parameter в DTO, передаваемый WEB и Maintenance.
    ///
    /// Важно передавать не только Type, но и Role.
    /// Type отвечает на вопрос: "какого типа это значение?"
    /// Role отвечает на вопрос: "что это за значение с точки зрения алгоритма?"
    ///
    /// Благодаря Role специализированный WEB UI Density/Capacity не должен знать конкретные имена temperatureC/pressureBarAbsolute.
    /// Он просто отображает все параметры Role == ProcessInput.
    /// </summary>
    private static CalcParameterDefinitionDto ToParameterDefinitionDto(CalculationParameterDefinition parameter)
    {
        var options = parameter.Options ?? Array.Empty<CalculationParameterOption>();

        return new CalcParameterDefinitionDto
        {
            Key = parameter.Key,
            Name = parameter.Name,
            Type = ToParameterTypeDto(parameter.Type),
            Role = ToParameterRoleDto(parameter.Role),
            AppliesToSubstanceCode = parameter.AppliesToSubstanceCode,

            Unit = parameter.Unit,
            IsRequired = parameter.IsRequired,
            DefaultValue = ToJsonElement(parameter.DefaultValue),

            Minimum = parameter.Minimum,
            Maximum = parameter.Maximum,
            Step = parameter.Step,
            Decimals = parameter.Decimals,
            Order = parameter.Order,
            Description = parameter.Description,

            Options = options.Select(option => new CalcParameterOptionDto
            {
                Value = option.Value,
                Name = option.Name,
                Phase = option.Phase
            }).ToList()
        };
    }

    /// <summary>
    /// Преобразует внутреннюю роль Calculation Parameter в транспортную модель Contracts.
    ///
    /// Отдельный switch используется намеренно:
    /// TechMES.Contracts не должен зависеть от TechMES.Calc, даже если enum сейчас содержит одинаковые значения.
    /// </summary>
    private static CalcParameterRoleDto ToParameterRoleDto(CalculationParameterRole role)
    {
        return role switch
        {
            CalculationParameterRole.Configuration => CalcParameterRoleDto.Configuration,
            CalculationParameterRole.ProcessInput => CalcParameterRoleDto.ProcessInput,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported calculation parameter role.")
        };
    }

    /// <summary>
    /// Преобразует внутренний тип входного параметра.
    /// </summary>
    private static CalcParameterTypeDto ToParameterTypeDto(CalculationParameterType type)
    {
        return type switch
        {
            CalculationParameterType.Number =>
                CalcParameterTypeDto.Number,

            CalculationParameterType.Integer =>
                CalcParameterTypeDto.Integer,

            CalculationParameterType.Boolean =>
                CalcParameterTypeDto.Boolean,

            CalculationParameterType.Selection =>
                CalcParameterTypeDto.Selection,

            CalculationParameterType.Text =>
                CalcParameterTypeDto.Text,

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported calculation parameter type.")
        };
    }

    /// <summary>
    /// Преобразует внутренний уровень сообщения.
    /// </summary>
    private static CalcMessageSeverityDto ToMessageSeverityDto(CalculationMessageSeverity severity)
    {
        return severity switch
        {
            CalculationMessageSeverity.Information =>
                CalcMessageSeverityDto.Information,

            CalculationMessageSeverity.Warning =>
                CalcMessageSeverityDto.Warning,

            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported calculation message severity.")
        };
    }

    /// <summary>
    /// Преобразует CLR-значение по умолчанию в JSON.
    /// </summary>
    private static JsonElement? ToJsonElement(object? value)
    {
        if (value is null)
            return null;

        return JsonSerializer.SerializeToElement(value, value.GetType());
    }

    /// <summary>
    /// Преобразует один JsonElement в безопасное CLR-значение.
    ///
    /// Поддерживаются только простые значения, необходимые расчётам:
    /// number, string, boolean и null.
    /// Массивы и произвольные JSON-объекты пока запрещены.
    /// </summary>
    private static object? ConvertJsonValue(string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.String:
                return value.GetString();

            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integerValue))
                    return integerValue;

                if (value.TryGetDecimal(out var decimalValue))
                    return decimalValue;

                if (value.TryGetDouble(out var doubleValue))
                    return doubleValue;

                throw new CalculationException(
                    "parameter.invalid-number",
                    $"Calculation parameter '{key}' contains an invalid number.");

            case JsonValueKind.Undefined:
            case JsonValueKind.Object:
            case JsonValueKind.Array:
            default:
                throw new CalculationException(
                    "parameter.json-type-unsupported",
                    $"Calculation parameter '{key}' must be a number, string, boolean or null.");
        }
    }
}