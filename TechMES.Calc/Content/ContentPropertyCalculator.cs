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
/// После Content 3 все production Content-корреляции выполняются новой архитектурой.
///
/// Legacy ContentCalc хранится только в TechMES.Calc.Tests и используется исключительно как regression oracle.
/// </summary>
public static class ContentPropertyCalculator
{
    public static IReadOnlyList<double> CalculatePercent(ContentCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Components);

        if (!double.IsFinite(request.TemperatureC))
            throw new CalculationException("content.temperature.invalid", "Content temperature must be a finite number.");

        if (!double.IsFinite(request.PressureBarAbsolute))
            throw new CalculationException("content.pressure.invalid", "Content pressure must be a finite number.");

        var components = NormalizeAndValidateComponents(request.Components);
        var temperature = (float)request.TemperatureC;
        var pressure = (float)request.PressureBarAbsolute;

        if (!ContentCombinationCalculator.TryCalculatePercent(components, temperature, pressure, request.ConfigurationCode, out var result))
            throw new CalculationException("content.components.unsupported", $"Content correlation is not defined for [{string.Join(", ", components)}].");

        return ValidateResult(result, components.Length);
    }

    /// <summary>
    /// Общая проверка результата любой Content-корреляции.
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
}