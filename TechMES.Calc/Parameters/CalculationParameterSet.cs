using System.Globalization;
using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Parameters;

/// <summary>
/// Содержит фактические значения параметров одного запуска расчёта.
///
/// Имена параметров сравниваются без учёта регистра.
/// Например, temperature, Temperature и TEMPERATURE считаются
/// одним и тем же параметром.
/// </summary>
public sealed class CalculationParameterSet
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public CalculationParameterSet(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var item in values)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                throw new CalculationException("parameter.key-empty", "Calculation parameter key cannot be empty.");
            }

            var key = item.Key.Trim();

            if (!_values.TryAdd(key, item.Value))
            {
                throw new CalculationException("parameter.duplicate", $"Calculation parameter '{key}' is specified more than once.");
            }
        }
    }

    /// <summary>
    /// Возвращает неизменяемое представление всех переданных значений.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>
    /// Проверяет наличие параметра независимо от регистра его имени.
    /// </summary>
    public bool Contains(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && _values.ContainsKey(key);
    }

    /// <summary>
    /// Пытается получить исходное значение параметра.
    /// </summary>
    public bool TryGetValue(string key, out object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            value = null;
            return false;
        }

        return _values.TryGetValue(key, out value);
    }

    /// <summary>
    /// Возвращает обязательное числовое значение.
    /// </summary>
    public double GetRequiredDouble(string key)
    {
        var value = GetRequiredValue(key);
        var result = ConvertToDouble(key, value);

        if (!double.IsFinite(result))
        {
            throw new CalculationException("parameter.not-finite", $"Calculation parameter '{key}' must be a finite number.");
        }

        return result;
    }

    /// <summary>
    /// Возвращает числовое значение либо заданное значение по умолчанию.
    /// </summary>
    public double GetDouble(string key, double defaultValue)
    {
        return TryGetNonNullValue(key, out var value)
            ? ConvertToDouble(key, value)
            : defaultValue;
    }

    /// <summary>
    /// Возвращает обязательное целое значение.
    /// </summary>
    public int GetRequiredInt(string key)
    {
        var number = GetRequiredDouble(key);

        if (number < int.MinValue
            || number > int.MaxValue
            || Math.Truncate(number) != number)
        {
            throw new CalculationException("parameter.invalid-integer", $"Calculation parameter '{key}' must be an integer.");
        }

        return (int)number;
    }

    /// <summary>
    /// Возвращает целое значение либо заданное значение по умолчанию.
    /// </summary>
    public int GetInt(string key, int defaultValue)
    {
        return TryGetNonNullValue(key, out _)
            ? GetRequiredInt(key)
            : defaultValue;
    }

    /// <summary>
    /// Возвращает обязательное логическое значение.
    ///
    /// Поддерживаются bool, строки true/false и числовые значения 1/0.
    /// </summary>
    public bool GetRequiredBoolean(string key)
    {
        var value = GetRequiredValue(key);

        if (value is bool boolean)
            return boolean;

        if (value is string text)
        {
            if (bool.TryParse(text, out var parsedBoolean))
                return parsedBoolean;

            if (text.Trim() == "1")
                return true;

            if (text.Trim() == "0")
                return false;
        }

        if (TryConvertToDouble(value, out var number))
        {
            return number switch
            {
                0 => false,
                1 => true,
                _ => throw new CalculationException("parameter.invalid-boolean", $"Calculation parameter '{key}' must be true, false, 1 or 0.")
            };
        }

        throw new CalculationException("parameter.invalid-boolean", $"Calculation parameter '{key}' must be true, false, 1 or 0.");
    }

    /// <summary>
    /// Возвращает логическое значение либо заданное значение по умолчанию.
    /// </summary>
    public bool GetBoolean(string key, bool defaultValue)
    {
        return TryGetNonNullValue(key, out _)
            ? GetRequiredBoolean(key)
            : defaultValue;
    }

    /// <summary>
    /// Возвращает обязательное непустое строковое значение.
    /// </summary>
    public string GetRequiredString(string key)
    {
        var value = GetRequiredValue(key);

        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new CalculationException("parameter.invalid-text", $"Calculation parameter '{key}' must contain text.");
        }

        return text.Trim();
    }

    /// <summary>
    /// Возвращает строковое значение либо заданное значение по умолчанию.
    /// </summary>
    public string GetString(string key, string defaultValue)
    {
        return TryGetNonNullValue(key, out _)
            ? GetRequiredString(key)
            : defaultValue;
    }

    /// <summary>
    /// Возвращает обязательное исходное значение параметра.
    /// Null считается отсутствующим значением.
    /// </summary>
    private object GetRequiredValue(string key)
    {
        if (!TryGetNonNullValue(key, out var value))
        {
            throw new CalculationException("parameter.missing", $"Required calculation parameter '{key}' is missing.");
        }

        return value;
    }

    /// <summary>
    /// Проверяет, что параметр существует и не содержит null.
    /// </summary>
    private bool TryGetNonNullValue(string key, out object value)
    {
        if (!string.IsNullOrWhiteSpace(key) && _values.TryGetValue(key, out var storedValue) && storedValue is not null)
        {
            value = storedValue;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// Преобразует поддерживаемое значение в double.
    /// </summary>
    private static double ConvertToDouble(string key, object value)
    {
        if (!TryConvertToDouble(value, out var result))
        {
            throw new CalculationException("parameter.invalid-number", $"Calculation parameter '{key}' must be numeric.");
        }

        if (!double.IsFinite(result))
        {
            throw new CalculationException("parameter.not-finite", $"Calculation parameter '{key}' must be a finite number.");
        }

        return result;
    }

    /// <summary>
    /// Выполняет безопасное преобразование основных числовых типов
    /// и строк с инвариантным десятичным разделителем.
    ///
    /// Значения, приходящие из WEB, должны передаваться в JSON как числа,
    /// поэтому локаль операционной системы на результат не влияет.
    /// </summary>
    private static bool TryConvertToDouble(object value, out double result)
    {
        switch (value)
        {
            case double number:
                result = number;
                return true;

            case float number:
                result = number;
                return true;

            case decimal number:
                result = (double)number;
                return true;

            case byte number:
                result = number;
                return true;

            case sbyte number:
                result = number;
                return true;

            case short number:
                result = number;
                return true;

            case ushort number:
                result = number;
                return true;

            case int number:
                result = number;
                return true;

            case uint number:
                result = number;
                return true;

            case long number:
                result = number;
                return true;

            case ulong number:
                result = number;
                return true;

            case string text when double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed):
                result = parsed;
                return true;

            default:
                result = default;
                return false;
        }
    }
}