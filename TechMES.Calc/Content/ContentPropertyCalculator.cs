using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Content;

/// <summary>
/// Безопасная оболочка Content-корреляций TechMES.Calc.
///
/// Контракт:
/// - Temperature: °C;
/// - Pressure: bar(abs);
/// - порядок компонентов сохраняется;
/// - наружу возвращаются инженерные проценты;
/// - некорректные значения приводят к CalculationException.
///
/// После Content 2 все бинарные системы выполняются новой архитектурой.
///
/// Временный legacy fallback остаётся только для тройной системы
/// ACN + Water + PO до этапа Content 3.
/// </summary>
public static class ContentPropertyCalculator
{
    public static IReadOnlyList<double> CalculatePercent(ContentCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Components);

        if (!double.IsFinite(request.TemperatureC))
            throw new CalculationException("content.temperature.invalid", "Content temperature must be a finite number.");

        if (!double.IsFinite(request.PressureBarAbsolute) || request.PressureBarAbsolute <= 0d)
            throw new CalculationException("content.pressure.invalid", "Content absolute pressure must be greater than zero.");

        var components = NormalizeAndValidateComponents(request.Components);

        var temperature = (float)request.TemperatureC;
        var pressure = (float)request.PressureBarAbsolute;
        var configurationCode = request.ConfigurationCode;

        // Новые Content-модели.
        if (ContentCombinationCalculator.TryCalculatePercent(components, temperature, pressure,configurationCode, out var migratedResult))
            return ValidateResult(migratedResult, components.Length);

        // ------------------------------------------------------------
        // ВРЕМЕННЫЙ LEGACY FALLBACK
        // ------------------------------------------------------------
        // После Content 2 здесь остаётся только тройная система.
        // После Content 3 этот блок должен быть полностью удалён.
        // ------------------------------------------------------------

        double[]? raw = null;

        if (Match(components, "ACN", "Water", "PO"))
        {
            raw = ContentCalc.ACN_Water_PO_Content(temperature, pressure, configurationCode);
        }
        else if (Match(components, "PO", "Water", "ACN"))
        {
            raw = ContentCalc.PO_Water_ACN_Content(temperature, pressure, configurationCode);
        }

        if (raw is null)
            throw new CalculationException("content.components.unsupported", $"Content correlation is not defined for [{string.Join(", ", components)}].");

        if (raw.Length < components.Length)
            throw new CalculationException("content.result.invalid-count", $"Content correlation returned {raw.Length} values for {components.Length} configured components.");

        var result = raw.Take(components.Length).Select(value => value / 100d).ToArray();

        return ValidateResult(result, components.Length);
    }

    /// <summary>
    /// Проверяет результат как новой Content-модели, так и временного legacy fallback.
    /// Это также закрывает проблему Content 1:
    /// после double -> float очень большое конечное double может превратиться в Infinity.
    /// Поэтому результат корреляции обязательно проверяется непосредственно перед возвратом наружу.
    /// </summary>
    private static IReadOnlyList<double> ValidateResult(IReadOnlyList<double> source, int expectedCount)
    {
        if (source.Count < expectedCount)
            throw new CalculationException("content.result.invalid-count", $"Content correlation returned {source.Count} values for {expectedCount} configured components.");

        var result = source.Take(expectedCount).ToArray();

        if (result.Any(value => !double.IsFinite(value)))
            throw new CalculationException("content.result.invalid", "Content correlation returned a non-finite value.");

        return result;
    }

    private static string[] NormalizeAndValidateComponents(IReadOnlyList<string> source)
    {
        if (source.Count is < 2 or > 3)
            throw new CalculationException("content.components.invalid-count", "Content calculation requires two or three configured components.");

        var result = new string[source.Count];
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < source.Count; index++)
        {
            var code = source[index];

            if (string.IsNullOrWhiteSpace(code))
                throw new CalculationException("content.component.code-empty", $"Content component at position {index + 1} cannot be empty.");

            code = code.Trim();

            if (!usedCodes.Add(code))
                throw new CalculationException("content.component.duplicate", $"Content component '{code}' is specified more than once.");

            SubstanceCatalog.GetRequired(code);
            result[index] = code;
        }

        return result;
    }

    private static bool Match(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length)
            return false;

        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}