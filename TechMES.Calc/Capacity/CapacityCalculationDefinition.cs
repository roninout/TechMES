using TechMES.Calc.Constants;
using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;
using TechMES.Calc.Substances.Components;

namespace TechMES.Calc.Capacity;

/// <summary>
/// Расчёт удельной теплоёмкости многокомпонентной смеси.
///
/// По структуре Calculation Job Capacity намеренно повторяет Density:
/// - Temperature;
/// - optional Pressure;
/// - до трёх дополнительных ProcessInput;
/// - CompN / Perc0..Perc4;
/// - component0Code..component4Code;
/// - DeltaC;
/// - один основной SCADA output Capacity.
///
/// Математика смеси при этом своя:
///
///     Cp = Σ(w_i × Cp_i)
///
/// Legacy GetCapacity возвращает kJ/(kg·K), а MixturePropertyCalculator нормализует компонентные и итоговый результаты в J/(kg·K).
/// </summary>
public sealed class CapacityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.capacity";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarGauge";
    private const string CorrectionKey = "capacityCorrection";

    private static readonly string[] AdditionalParameterKeys =
    [
        DryMatter.PurityParameterKey,
        "additionalParameter2",
        "additionalParameter3"
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> PropertyParameterDefinitions =
    [
        new CalculationParameterDefinition(
            Key: TemperatureKey,
            Name: "Temperature",
            Type: CalculationParameterType.Number,
            Unit: "°C",
            IsRequired: true,
            Minimum: -273.15,
            Step: 0.1,
            Decimals: 2,
            Order: 1,
            Description: "Mixture temperature used by the substance heat-capacity correlations.",
            Role: CalculationParameterRole.ProcessInput),

        // Старый Capacity, как и Density, получал Pressure.Val_R как избыточное давление. Абсолютное давление формировалось перед вызовом TechDotNetLib.Mix:
        //     P(abs) = P(g) + Patm.
        // Текущие legacy GetCapacity физически используют только Temperature, поэтому Pressure пока является зарезервированным ProcessInput.
        // Но контракт сохраняем правильным уже сейчас, чтобы будущая Cp-модель могла использовать Pressure без изменения Job configuration.
        new CalculationParameterDefinition(
            Key: PressureKey, 
            Name: "Pressure", 
            Type: CalculationParameterType.Number, 
            Unit: "bar(g)",
            IsRequired: false, DefaultValue: 0d, Step: 0.01, Decimals: 4, 
            Order: 2,
            Description: "Optional gauge pressure. Absolute pressure is calculated by adding the configured atmospheric pressure.",
            Role: CalculationParameterRole.ProcessInput),

        // Purity используется CSS-корреляцией DryMatter.
        // Для остальных веществ этот ProcessInput просто не участвует в их legacy GetCapacity.
        // Если тег не настроен, CalculationParameterValidator автоматически подставляет DefaultValue = 90%.
        new CalculationParameterDefinition(
            Key: DryMatter.PurityParameterKey,
            Name: "Purity",
            Type: CalculationParameterType.Number,
            Unit: "%",
            IsRequired: false,
            DefaultValue: DryMatter.DefaultPurityPercent,
            Minimum: 0,
            Maximum: 100,
            Step: 0.1,
            Decimals: 1,
            Order: 3,
            Description: "Dry matter purity used by the sugar solution specific heat capacity correlation.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter2",
            Name: "Additional parameter",
            Type: CalculationParameterType.Number,
            IsRequired: false,
            Order: 4,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter3",
            Name: "Additional parameter",
            Type: CalculationParameterType.Number,
            IsRequired: false,
            Order: 5,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        // Legacy DELTA_C:
        //
        // OPC read scaling ×10 и последующее ×0.1 в старом расчёте
        // взаимно уничтожались. Поэтому новый Job хранит DeltaC сразу
        // как инженерное J/(kg·K) значение.
        new CalculationParameterDefinition(
            Key: CorrectionKey,
            Name: "DeltaC",
            Type: CalculationParameterType.Number,
            Unit: "J/(kg·K)",
            IsRequired: false,
            DefaultValue: 0d,
            Step: 1,
            Decimals: 3,
            Order: 10,
            Description: "Specific heat capacity correction read from the Capacity Equipment DeltaC SCADA item.",
            Role: CalculationParameterRole.Configuration)
    ];

    // Capacity получает только те вещества, для которых SpecificHeatCapacity явно разрешена SubstanceCatalog.
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateMixtureParameters(PropertyParameterDefinitions, SubstancePropertySupport.SpecificHeatCapacity);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
            Key: "capacity",
            Name: "Specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 1,
            Description: "Calculated mixture specific heat capacity including DeltaC."),

        // Diagnostic Runtime/UI outputs.
        new CalculationOutputDefinition(
            Key: "component0Capacity",
            Name: "Component 1 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 101,
            Description: "Specific heat capacity of mixture component 1."),

        new CalculationOutputDefinition(
            Key: "component1Capacity",
            Name: "Component 2 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 102,
            Description: "Specific heat capacity of mixture component 2."),

        new CalculationOutputDefinition(
            Key: "component2Capacity",
            Name: "Component 3 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 103,
            Description: "Specific heat capacity of mixture component 3."),

        new CalculationOutputDefinition(
            Key: "component3Capacity",
            Name: "Component 4 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 104,
            Description: "Specific heat capacity of mixture component 4."),

        new CalculationOutputDefinition(
            Key: "component4Capacity",
            Name: "Component 5 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 105,
            Description: "Specific heat capacity of mixture component 5.")
    ];

    public override string Code => DefinitionCode;
    public override string Name => "Mixture specific heat capacity";
    public override string Category => "Capacity";

    // Version 2:
    // - Capacity component options filter unsupported Cp models;
    // - ProcessInput count expanded to the same 2..5 contract as Density;
    // - componentNCapacity diagnostic outputs added;
    // - Density-only DryMatter validation removed from Capacity path.
    public override string Version => "3";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarGauge = parameters.GetDouble(PressureKey, 0d);
        var pressureBarAbsolute = pressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;
        var deltaC = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);
        var additionalParameters = ReadAdditionalParameters(parameters);

        var mixtureResult = MixturePropertyCalculator.CalculateSpecificHeatCapacity(components, temperatureC, additionalParameters);

        var baseCapacityJPerKgK = mixtureResult.SpecificHeatCapacityJPerKgK;
        var capacityJPerKgK = baseCapacityJPerKgK + deltaC;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
            return CalculationResult.Failure("capacity.result.invalid", "Calculated specific heat capacity after DeltaC must be a finite value greater than zero.");

        var outputs = new List<CalculationOutput>
        {
            new(Key: "capacity", Name: "Specific heat capacity", Value: capacityJPerKgK, Unit: "J/(kg·K)")
        };

        foreach (var component in mixtureResult.Components)
        {
            outputs.Add(new CalculationOutput(Key: $"component{component.Index}Capacity", Name: $"{component.SubstanceCode} specific heat capacity", Value: component.SpecificHeatCapacityJPerKgK, Unit: "J/(kg·K)"));
        }

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem("temperatureC", "Temperature", Format(temperatureC), "°C"));
            // Pressure пока диагностический ProcessInput.
            trace.Add(new CalculationTraceItem("pressureBarGauge", "Pressure", Format(pressureBarGauge), "bar(g)"));
            trace.Add(new CalculationTraceItem("pressureBarAbsolute", "Absolute pressure", Format(pressureBarAbsolute), "bar(abs)"));

            foreach (var parameter in additionalParameters.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                trace.Add(new CalculationTraceItem(parameter.Key, "Additional parameter", Format(parameter.Value),null));
            }

            AddMixtureTrace(trace, components);

            foreach (var component in mixtureResult.Components)
            {
                trace.Add(new CalculationTraceItem($"component{component.Index}Capacity", $"{component.SubstanceCode} specific heat capacity", Format(component.SpecificHeatCapacityJPerKgK), "J/(kg·K)"));
            }

            trace.Add(new CalculationTraceItem("baseCapacity", "Capacity before DeltaC", Format(baseCapacityJPerKgK), "J/(kg·K)"));
            trace.Add(new CalculationTraceItem("capacityCorrection", "DeltaC", Format(deltaC), "J/(kg·K)"));
            trace.Add(new CalculationTraceItem("capacity", "Final specific heat capacity", Format(capacityJPerKgK), "J/(kg·K)"));
        }

        return CalculationResult.Success(outputs, trace: trace);
    }

    private static IReadOnlyDictionary<string, double> ReadAdditionalParameters(CalculationParameterSet parameters)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in AdditionalParameterKeys)
        {
            if (parameters.TryGetValue(key, out var rawValue) && rawValue is not null)
                result[key] = parameters.GetRequiredDouble(key);
        }

        return result;
    }
}