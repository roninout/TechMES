using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Content;

/// <summary>
/// Новая безопасная оболочка над legacy-корреляциями Content из TechDotNetLib.
///
/// Сам ContentCalc хранит исходные полиномы, коэффициенты и алгоритмы интерполяции.
/// Мы намеренно не меняем их внутри legacy-слоя.
///
/// Этот класс отвечает уже за новый контракт TechMES.Calc:
/// - Temperature передаётся в °C;
/// - Pressure передаётся в bar(abs);
/// - порядок компонентов сохраняется;
/// - некорректная конфигурация приводит к CalculationException;
/// - старый SCADA scaling 0..10000 убирается;
/// - наружу возвращается содержание компонентов в инженерных процентах.
/// </summary>
public static class ContentPropertyCalculator
{
    /// <summary>
    /// Выполняет расчёт Content для одной из комбинаций компонентов,
    /// которые были реализованы в старом TechDotNetLib.
    ///
    /// Порядок Components принципиален:
    /// ACN + Water и Water + ACN являются разными конфигурациями,
    /// потому что порядок одновременно определяет порядок выходных результатов.
    ///
    /// Поэтому пустые элементы нельзя просто удалить из массива.
    /// Например [PO, "", Water] является ошибочной конфигурацией,
    /// а не эквивалентом [PO, Water].
    /// </summary>
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

        double[]? raw = null;

        if (Match(components, "ALC", "Water"))
            raw = ContentCalc.ALC_Water_Content(temperature, pressure, configurationCode);
        else if (Match(components, "ACN", "Water"))
            raw = ContentCalc.ACN_Water_Content(temperature, pressure, configurationCode);
        else if (Match(components, "Water", "ACN"))
            raw = ContentCalc.Water_ACN_Content(temperature, pressure, configurationCode);
        else if (Match(components, "PO", "P"))
            raw = ContentCalc.PO_P_Content(temperature, pressure, configurationCode);
        else if (Match(components, "P", "PO"))
            raw = ContentCalc.P_PO_Content(temperature, pressure, configurationCode);
        else if (Match(components, "ACN", "Water", "PO"))
            raw = ContentCalc.ACN_Water_PO_Content(temperature, pressure, configurationCode);
        else if (Match(components, "PO", "Water", "ACN"))
            raw = ContentCalc.PO_Water_ACN_Content(temperature, pressure, configurationCode);
        else if (Match(components, "PO", "Water"))
            raw = ContentCalc.PO_Water_Content(temperature, pressure, configurationCode);
        else if (Match(components, "Water", "PO"))
            raw = ContentCalc.Water_PO_Content(temperature, pressure, configurationCode);
        else if (Match(components, "ACA", "PO"))
            raw = ContentCalc.ACA_PO_Content(temperature, pressure, configurationCode);
        else if (Match(components, "PO", "ACA"))
            raw = ContentCalc.PO_ACA_Content(temperature, pressure, configurationCode);

        if (raw is null)
            throw new CalculationException("content.components.unsupported", $"Content correlation is not defined for [{string.Join(", ", components)}].");

        if (raw.Length < components.Length)
            throw new CalculationException("content.result.invalid-count", $"Content correlation returned {raw.Length} values for {components.Length} configured components.");

        // Legacy ContentCalc хранит содержание в сотых долях процента:
        // 10000 = 100.00%.
        //
        // В TechMES.Calc транспортный SCADA scaling не используется,
        // поэтому делим legacy-результат на 100 и возвращаем обычные проценты.
        var result = raw.Take(components.Length).Select(value => value / 100d).ToArray();

        if (result.Any(value => !double.IsFinite(value)))
            throw new CalculationException("content.result.invalid", "Content correlation returned a non-finite value.");

        return result;
    }

    /// <summary>
    /// Нормализует список кодов компонентов, не изменяя их количество и порядок.
    ///
    /// В отличие от предыдущего варианта здесь намеренно нет Where(...),
    /// потому что удаление пустого элемента меняет смысл Content-конфигурации.
    /// </summary>
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

    /// <summary>
    /// Сравнивает фактическую конфигурацию компонентов с одной конкретной
    /// legacy-корреляцией без учёта регистра символов.
    ///
    /// Порядок при этом обязательно учитывается.
    /// </summary>
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