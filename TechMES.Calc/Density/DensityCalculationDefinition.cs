using TechMES.Calc.Constants;
using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Density;

/// <summary>
/// Расчёт плотности многокомпонентной смеси.
///
/// Логика Pressure повторяет старый TechParamsCalc:
///
///     P(abs) = P(g) + Patm
///
/// где:
/// P(g)  - значение связанного SCADA Pressure.R;
/// Patm  - CalculationPhysicalConstants.AtmosphericPressureBarAbsolute.
///
/// Если Pressure tag не настроен, P(g) = 0,
/// поэтому расчёт выполняется при атмосферном абсолютном давлении.
///
/// Состав смеси:
/// - CompN и Perc0...Perc4 читаются из SCADA;
/// - component0Code...component4Code хранятся в конфигурации TechMES.
///
/// DeltaD читается из SCADA и прибавляется к инженерному результату Density.
///
/// Единственный output:
/// density -> Density.ValCalc.
/// </summary>
public sealed class DensityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.density";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarGauge";
    private const string CorrectionKey = "densityCorrection";

    private static readonly string[] AdditionalParameterKeys =
    [
        "additionalParameter1",
        "additionalParameter2",
        "additionalParameter3"
    ];

    /// <summary>
    /// Параметры физической модели Density.
    ///
    /// Temperature и Pressure повторяют контракт старого TechParamsCalc.
    /// Три Additional parameter зарезервированы для будущих веществ,
    /// которым понадобятся дополнительные технологические параметры.
    /// </summary>
    private static readonly IReadOnlyList<CalculationParameterDefinition> PropertyParameterDefinitions =
    [
        new CalculationParameterDefinition(
            Key: TemperatureKey, Name: "Temperature", Type: CalculationParameterType.Number, Unit: "°C",
            IsRequired: true, Minimum: -273.15, Step: 0.1, Decimals: 2, Order: 1,
            Description: "Mixture temperature used by the substance density correlations.",
            Role: CalculationParameterRole.ProcessInput),

        // В старом TechParamsCalc Pressure.Val_R передавал измеренное
        // избыточное давление, а абсолютное давление формировалось
        // непосредственно перед вызовом TechDotNetLib.Mix:
        //
        // Pressure.Val_R + AtmoPressure.
        //
        // Поэтому и здесь ProcessInput хранит именно gauge pressure.
        // Если binding отсутствует, используется 0 bar(g).
        new CalculationParameterDefinition(
            Key: PressureKey, Name: "Pressure", Type: CalculationParameterType.Number, Unit: "bar(g)",
            IsRequired: false, DefaultValue: 0d, Step: 0.01, Decimals: 4, Order: 2,
            Description: "Optional gauge pressure. Absolute pressure is calculated by adding the configured atmospheric pressure.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter1", Name: "Additional parameter", Type: CalculationParameterType.Number,
            IsRequired: false, Order: 3,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter2", Name: "Additional parameter", Type: CalculationParameterType.Number,
            IsRequired: false, Order: 4,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter3", Name: "Additional parameter", Type: CalculationParameterType.Number,
            IsRequired: false, Order: 5,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        // DeltaD остаётся read-only SCADA input.
        new CalculationParameterDefinition(
            Key: CorrectionKey, Name: "DeltaD", Type: CalculationParameterType.Number, Unit: "kg/m³",
            IsRequired: false, DefaultValue: 0d, Step: 0.1, Decimals: 3, Order: 10,
            Description: "Density correction read from the Density Equipment DeltaD SCADA item.",
            Role: CalculationParameterRole.Configuration)
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateMixtureParameters(PropertyParameterDefinitions, SubstancePropertySupport.Density);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
        Key: "density", Name: "Density", Unit: "kg/m³", Decimals: 3, Order: 1,
        Description: "Calculated mixture density including SCADA DeltaD."),

    // Диагностические outputs.
    //
    // Они не имеют SCADA output binding и используются только для Runtime/UI.
    // componentN соответствует тому же SCADA слоту, что componentNCode/PercN.
    new CalculationOutputDefinition(
        Key: "component0Density", Name: "Component 1 density", Unit: "kg/m³", Decimals: 3, Order: 101,
        Description: "Calculated density of mixture component 1."),

    new CalculationOutputDefinition(
        Key: "component1Density", Name: "Component 2 density", Unit: "kg/m³", Decimals: 3, Order: 102,
        Description: "Calculated density of mixture component 2."),

    new CalculationOutputDefinition(
        Key: "component2Density", Name: "Component 3 density", Unit: "kg/m³", Decimals: 3, Order: 103,
        Description: "Calculated density of mixture component 3."),

    new CalculationOutputDefinition(
        Key: "component3Density", Name: "Component 4 density", Unit: "kg/m³", Decimals: 3, Order: 104,
        Description: "Calculated density of mixture component 4."),

    new CalculationOutputDefinition(
        Key: "component4Density", Name: "Component 5 density", Unit: "kg/m³", Decimals: 3, Order: 105,
        Description: "Calculated density of mixture component 5.")
    ];

    public override string Code => DefinitionCode;
    public override string Name => "Mixture density";
    public override string Category => "Density";

    // Version 4:
    // - Density options фильтруются по SubstancePropertySupport.Density;
    // - ACA / ACAS с отсутствующей legacy Density больше не предлагаются;
    // - Methan / Fusel используют нормализованный TechMES contract °C / bar(abs)
    //   с внутренней адаптацией в native TechDotNet K / Pa.
    public override string Version => "4";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Выполняет физический расчёт Density.
    ///
    /// Последовательность соответствует старому TechParamsCalc:
    ///
    /// 1. Получаем Temperature.
    /// 2. Получаем Pressure.R как gauge pressure или 0.
    /// 3. Формируем absolute pressure:
    ///        P(abs) = P(g) + Patm.
    /// 4. Формируем смесь из CompN/PercN/componentNCode.
    /// 5. Считаем фактическую Density каждого компонента.
    /// 6. Считаем смесь по 1 / Σ(w_i / rho_i).
    /// 7. Прибавляем инженерный DeltaD.
    ///
    /// Кроме основного density возвращаем componentNDensity.
    /// Это диагностические Runtime outputs для верхней WEB-панели.
    /// Они не записываются в SCADA.
    ///
    /// Старое ×10 применяется только Runtime output binding при записи ValCalc.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarGauge = parameters.GetDouble(PressureKey, 0d);
        var pressureBarAbsolute = pressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;
        var deltaD = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);
        var additionalParameters = ReadAdditionalParameters(parameters);

        var mixtureResult = MixturePropertyCalculator.CalculateDensity(components, temperatureC, pressureBarAbsolute, additionalParameters);
        var baseDensityKgPerM3 = mixtureResult.DensityKgPerM3;
        var densityKgPerM3 = baseDensityKgPerM3 + deltaD;

        if (!double.IsFinite(densityKgPerM3) || densityKgPerM3 <= 0d)
            return CalculationResult.Failure("density.result.invalid", "Calculated density after DeltaD must be a finite value greater than zero.");

        var outputs = new List<CalculationOutput>
    {
        new CalculationOutput(
            Key: "density",
            Name: "Density",
            Value: densityKgPerM3,
            Unit: "kg/m³")
    };

        // В LastOutputs сохраняем также фактическую Density каждого
        // участвующего компонента.
        //
        // Index сохраняет соответствие componentNCode/PercN.
        foreach (var component in mixtureResult.Components)
        {
            outputs.Add(new CalculationOutput(
                Key: $"component{component.Index}Density",
                Name: $"{component.SubstanceCode} density",
                Value: component.DensityKgPerM3,
                Unit: "kg/m³"));
        }

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem("temperatureC", "Temperature", Format(temperatureC), "°C"));
            trace.Add(new CalculationTraceItem("pressureBarGauge", "Pressure", Format(pressureBarGauge), "bar(g)"));
            trace.Add(new CalculationTraceItem("pressureBarAbsolute", "Absolute pressure", Format(pressureBarAbsolute), "bar(abs)"));

            foreach (var parameter in additionalParameters.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                trace.Add(new CalculationTraceItem(parameter.Key, "Additional parameter", Format(parameter.Value), null));

            AddMixtureTrace(trace, components);

            foreach (var component in mixtureResult.Components)
                trace.Add(new CalculationTraceItem($"component{component.Index}Density", $"{component.SubstanceCode} density", Format(component.DensityKgPerM3), "kg/m³"));

            trace.Add(new CalculationTraceItem("baseDensity", "Density before DeltaD", Format(baseDensityKgPerM3), "kg/m³"));
            trace.Add(new CalculationTraceItem("densityCorrection", "DeltaD", Format(deltaD), "kg/m³"));
            trace.Add(new CalculationTraceItem("density", "Final density", Format(densityKgPerM3), "kg/m³"));
        }

        return CalculationResult.Success(outputs, trace: trace);
    }

    /// <summary>
    /// Собирает только фактически переданные дополнительные ProcessInput.
    ///
    /// Старые компоненты этот словарь игнорируют:
    /// базовая перегрузка LegacySubstance автоматически вызывает
    /// исходный GetDensity(float temperature, float pressure).
    ///
    /// Новый компонент может переопределить расширенную перегрузку
    /// GetDensity(..., additionalParameters) и использовать нужные ключи.
    /// </summary>
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
