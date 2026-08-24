namespace TechMES.Calc.Substances;

/// <summary>
/// One component of a mass-based mixture.
/// MassPercent is expressed in percent, e.g. 25 means 25 wt.%.
/// </summary>
public sealed record MixtureComponent(string SubstanceCode, double MassPercent);
