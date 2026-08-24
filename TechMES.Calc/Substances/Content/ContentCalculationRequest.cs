namespace TechMES.Calc.Substances.Content;

/// <summary>
/// Input for the legacy-compatible thermodynamic content correlations.
/// Temperature is °C and pressure is bar(abs).
/// </summary>
public sealed record ContentCalculationRequest(IReadOnlyList<string> Components, double TemperatureC, double PressureBarAbsolute, int ConfigurationCode);
