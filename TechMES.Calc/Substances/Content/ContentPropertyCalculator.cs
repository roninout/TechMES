using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances.Legacy.Content;

namespace TechMES.Calc.Substances.Content;

/// <summary>
/// Facade over the content correlations ported from TechDotNetLib.
///
/// The old library returned SCADA raw values where 10000 represented 100%.
/// This facade removes that transport scaling and returns engineering percent.
/// </summary>
public static class ContentPropertyCalculator
{
    public static IReadOnlyList<double> CalculatePercent(ContentCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Components);

        if (!double.IsFinite(request.TemperatureC))
            throw new CalculationException("content.temperature.invalid", "Content temperature must be a finite number.");

        if (!double.IsFinite(request.PressureBarAbsolute) || request.PressureBarAbsolute <= 0)
            throw new CalculationException("content.pressure.invalid", "Content absolute pressure must be greater than zero.");

        var components = request.Components
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToArray();

        if (components.Length is < 2 or > 3)
            throw new CalculationException("content.components.invalid-count", "Content calculation requires two or three configured components.");

        foreach (var component in components)
            SubstanceCatalog.GetRequired(component);

        var t = (float)request.TemperatureC;
        var p = (float)request.PressureBarAbsolute;
        var c = request.ConfigurationCode;

        double[]? raw = null;

        if (Match(components, "ALC", "Water"))
            raw = ContentCalc.ALC_Water_Content(t, p, c);
        else if (Match(components, "ACN", "Water"))
            raw = ContentCalc.ACN_Water_Content(t, p, c);
        else if (Match(components, "Water", "ACN"))
            raw = ContentCalc.Water_ACN_Content(t, p, c);
        else if (Match(components, "PO", "P"))
            raw = ContentCalc.PO_P_Content(t, p, c);
        else if (Match(components, "P", "PO"))
            raw = ContentCalc.P_PO_Content(t, p, c);
        else if (Match(components, "ACN", "Water", "PO"))
            raw = ContentCalc.ACN_Water_PO_Content(t, p, c);
        else if (Match(components, "PO", "Water", "ACN"))
            raw = ContentCalc.PO_Water_ACN_Content(t, p, c);
        else if (Match(components, "PO", "Water"))
            raw = ContentCalc.PO_Water_Content(t, p, c);
        else if (Match(components, "Water", "PO"))
            raw = ContentCalc.Water_PO_Content(t, p, c);
        else if (Match(components, "ACA", "PO"))
            raw = ContentCalc.ACA_PO_Content(t, p, c);
        else if (Match(components, "PO", "ACA"))
            raw = ContentCalc.PO_ACA_Content(t, p, c);

        if (raw is null)
            throw new CalculationException("content.components.unsupported", $"Content correlation is not defined for [{string.Join(", ", components)}].");

        var result = raw
            .Take(components.Length)
            .Select(value => value / 100d)
            .ToArray();

        if (result.Any(value => !double.IsFinite(value)))
            throw new CalculationException("content.result.invalid", "Content correlation returned a non-finite value.");

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
