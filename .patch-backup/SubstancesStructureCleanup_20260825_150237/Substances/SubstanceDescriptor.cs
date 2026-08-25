namespace TechMES.Calc.Substances;

/// <summary>
/// Stable description of one substance code inherited from TechDotNetLib.
/// Code is the value historically stored in COMP/PERC configuration.
/// </summary>
public sealed record SubstanceDescriptor(
    string Code,
    string Name,
    SubstancePhase Phase);
