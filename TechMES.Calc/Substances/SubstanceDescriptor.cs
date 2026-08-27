namespace TechMES.Calc.Substances;

/// <summary>
/// Stable description of one substance code inherited from TechDotNetLib.
/// Code is the value historically stored in COMP/PERC configuration.
///
/// SupportedProperties отделяет сам факт наличия legacy-класса
/// от разрешения использовать его в конкретном Production Calculation Definition.
/// </summary>
public sealed record SubstanceDescriptor(string Code, string Name, SubstancePhase Phase, SubstancePropertySupport SupportedProperties)
{
    public bool Supports(SubstancePropertySupport property)
    {
        return property != SubstancePropertySupport.None && (SupportedProperties & property) == property;
    }
}