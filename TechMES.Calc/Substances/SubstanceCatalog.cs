using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances.Components;

namespace TechMES.Calc.Substances;

/// <summary>
/// Catalog of substance codes used by the former TechDotNetLib.
///
/// The catalog owns:
/// - code -> formula-model mapping;
/// - physical phase;
/// - explicit property capabilities used by Calculation Definitions.
///
/// It does not know anything about SCADA tags, PostgreSQL or Calc Jobs.
/// </summary>
public static class SubstanceCatalog
{
    private const SubstancePropertySupport DefaultPropertySupport = SubstancePropertySupport.Density | SubstancePropertySupport.SpecificHeatCapacity;

    private sealed record Entry(SubstanceDescriptor Descriptor, Func<LegacySubstance> Factory);

    private static readonly IReadOnlyDictionary<string, Entry> Entries = CreateEntries();

    public static IReadOnlyList<SubstanceDescriptor> Items { get; } = Entries.Values
        .Select(entry => entry.Descriptor)
        .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Возвращает только вещества, явно разрешённые для указанного свойства.
    ///
    /// Это основной источник options для property-specific Calculation Definition.
    /// Благодаря этому WEB не должен угадывать поддержку вещества по имени,
    /// фазе или пробному выполнению legacy-формулы.
    /// </summary>
    public static IReadOnlyList<SubstanceDescriptor> GetSupported(SubstancePropertySupport property)
    {
        if (property == SubstancePropertySupport.None)
            return [];

        return Items.Where(item => item.Supports(property)).ToArray();
    }

    public static bool TryGet(string? code, out SubstanceDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(code) && Entries.TryGetValue(code.Trim(), out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static SubstanceDescriptor GetRequired(string code)
    {
        if (TryGet(code, out var descriptor))
            return descriptor;

        throw new CalculationException("substance.unknown", $"Unknown substance code '{code}'.");
    }

    internal static LegacySubstance CreateRequiredModel(string code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Entries.TryGetValue(code.Trim(), out var entry))
            return entry.Factory();

        throw new CalculationException("substance.unknown", $"Unknown substance code '{code}'.");
    }

    private static IReadOnlyDictionary<string, Entry> CreateEntries()
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        void Add(string code, string name, SubstancePhase phase, Func<LegacySubstance> factory, SubstancePropertySupport supportedProperties = DefaultPropertySupport)
        {
            result.Add(code, new Entry(new SubstanceDescriptor(code, name, phase, supportedProperties), factory));
        }

        Add("ALC", "Alcohol", SubstancePhase.Liquid, () => new Alcohol(false));
        Add("ALCS", "Alcohol", SubstancePhase.Vapor, () => new Alcohol(true));

        Add("ACA", "Acetaldehyde", SubstancePhase.Liquid, () => new Acetaldehyde(false));
        Add("ACAS", "Acetaldehyde", SubstancePhase.Vapor, () => new Acetaldehyde(true));

        Add("ACN", "Acetonitrile", SubstancePhase.Liquid, () => new Acetonitrile(false), DefaultPropertySupport | SubstancePropertySupport.Content);
        Add("ACNS", "Acetonitrile", SubstancePhase.Vapor, () => new Acetonitrile(true));

        Add("HP", "Hydrogen peroxide", SubstancePhase.Liquid, () => new HydrohenPeroxyde(false));
        Add("HPS", "Hydrogen peroxide", SubstancePhase.Vapor, () => new HydrohenPeroxyde(true));

        Add("N", "Nitrogen", SubstancePhase.Vapor, () => new Nitrogen());
        Add("O2", "Oxygen", SubstancePhase.Vapor, () => new Oxygen());

        Add("P", "Propylene", SubstancePhase.Liquid, () => new Propylene(false));
        Add("PS", "Propylene", SubstancePhase.Vapor, () => new Propylene(true));

        Add("PO", "Propylene oxide", SubstancePhase.Liquid, () => new PropyleneOxyde(false));
        Add("POS", "Propylene oxide", SubstancePhase.Vapor, () => new PropyleneOxyde(true));

        Add("Water", "Water", SubstancePhase.Liquid, () => new Water(false), DefaultPropertySupport | SubstancePropertySupport.Content);
        Add("WaterS", "Water", SubstancePhase.Vapor, () => new Water(true));

        Add("DryMatter", "Dry matter", SubstancePhase.Liquid, () => new DryMatter(), SubstancePropertySupport.Density | SubstancePropertySupport.SpecificHeatCapacity);

        Add("Butadiene_1_2", "1,2-Butadiene", SubstancePhase.Liquid, () => new Butadiene_1_2(false));
        Add("Butadiene_1_2S", "1,2-Butadiene", SubstancePhase.Vapor, () => new Butadiene_1_2(true));

        Add("Butadiene_1_3", "1,3-Butadiene", SubstancePhase.Liquid, () => new Butadiene_1_3(false));
        Add("Butadiene_1_3S", "1,3-Butadiene", SubstancePhase.Vapor, () => new Butadiene_1_3(true));

        Add("Butene_1", "1-Butene", SubstancePhase.Liquid, () => new Butene_1(false));
        Add("Butene_1S", "1-Butene", SubstancePhase.Vapor, () => new Butene_1(true));

        Add("Cis-2-Butene", "cis-2-Butene", SubstancePhase.Liquid, () => new Cis_2_Butene(false));
        Add("Cis-2-ButeneS", "cis-2-Butene", SubstancePhase.Vapor, () => new Cis_2_Butene(true));

        Add("Ethane", "Ethane", SubstancePhase.Liquid, () => new Ethane(false));
        Add("EthaneS", "Ethane", SubstancePhase.Vapor, () => new Ethane(true));

        Add("Ethylene", "Ethylene", SubstancePhase.Liquid, () => new Ethylene(false));
        Add("EthyleneS", "Ethylene", SubstancePhase.Vapor, () => new Ethylene(true));

        Add("Isobutane", "Isobutane", SubstancePhase.Liquid, () => new Isobutane(false));
        Add("IsobutaneS", "Isobutane", SubstancePhase.Vapor, () => new Isobutane(true));

        Add("Methyl-Acetylene", "Methyl acetylene", SubstancePhase.Liquid, () => new Methyl_Acetylene(false));
        Add("Methyl-AcetyleneS", "Methyl acetylene", SubstancePhase.Vapor, () => new Methyl_Acetylene(true));

        Add("n-Butane", "n-Butane", SubstancePhase.Liquid, () => new N_Butane(false));
        Add("n-ButaneS", "n-Butane", SubstancePhase.Vapor, () => new N_Butane(true));

        Add("n-Pentane", "n-Pentane", SubstancePhase.Liquid, () => new N_Pentane(false));
        Add("n-PentaneS", "n-Pentane", SubstancePhase.Vapor, () => new N_Pentane(true));

        Add("Propadiene", "Propadiene", SubstancePhase.Liquid, () => new Propadiene(false));
        Add("PropadieneS", "Propadiene", SubstancePhase.Vapor, () => new Propadiene(true));

        Add("Pr", "Propane", SubstancePhase.Liquid, () => new Propane(false));
        Add("PrS", "Propane", SubstancePhase.Vapor, () => new Propane(true));

        Add("Trans-2-Butene", "trans-2-Butene", SubstancePhase.Liquid, () => new Trans_2_Butene(false));
        Add("Trans-2-ButeneS", "trans-2-Butene", SubstancePhase.Vapor, () => new Trans_2_Butene(true));

        Add("Vinylacetylene", "Vinylacetylene", SubstancePhase.Liquid, () => new Vinylacetylene(false));
        Add("VinylacetyleneS", "Vinylacetylene", SubstancePhase.Vapor, () => new Vinylacetylene(true));

        Add("Freezium", "Freezium", SubstancePhase.Liquid, () => new Freezium(false));
        Add("Ethanol", "Ethanol", SubstancePhase.Liquid, () => new Ethanol(false));
        Add("Addition", "Addition (legacy Ethanol model)", SubstancePhase.Liquid, () => new Ethanol(false));
        Add("Diesel", "Diesel", SubstancePhase.Liquid, () => new Diesel(false));

        Add("NaOH", "Sodium hydroxide", SubstancePhase.Liquid, () => new NaOH(false));
        Add("NaOHS", "Sodium hydroxide", SubstancePhase.Vapor, () => new NaOH(true));

        Add("HCL", "Hydrochloric acid", SubstancePhase.Liquid, () => new HCL(false));
        Add("HCLS", "Hydrochloric acid", SubstancePhase.Vapor, () => new HCL(true));

        // Methan Capacity использует собственный legacy temperature contract (K), а Fusel.GetCapacity возвращает legacy sentinel -1.
        // Density пока оставляем доступной для обратной совместимости существующего
        // Density ядра и regression-тестов. Capacity Definition эти два вещества больше не показывает и Calculation Core их явно отклоняет.
        Add("Methan", "Methane (legacy native K/Pa model)", SubstancePhase.Vapor, () => new Methan(true), SubstancePropertySupport.Density);
        Add("Fusel", "Fusel oil (legacy native K/Pa model)", SubstancePhase.Liquid, () => new Fusel(false), SubstancePropertySupport.Density);

        return result;
    }
}