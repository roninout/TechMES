using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Capacity;

/// <summary>
/// Расчёт удельной теплоёмкости многокомпонентной смеси.
///
/// В старом TechParamsCalc объект Capacity содержал ссылки и на Temperature,
/// и на Pressure и вызывал Mix.GetCapacity(temperature, pressure).
///
/// Однако внутри старого Mix.GetCapacity Pressure фактически не использовалось:
/// каждый Substance.GetCapacity() получал только Temperature.
///
/// Поэтому в новой версии:
/// - Temperature является обязательным физическим входом;
/// - Pressure уже присутствует в контракте, но пока имеет DefaultValue
///   и не влияет на текущую legacy-формулу.
///
/// Это позволяет сохранить Pressure как полноценный ProcessInput уже сейчас,
/// не создавая при этом ложную зависимость существующей формулы от давления.
///
/// Как и Density, этот Definition не ограничен двумя физическими параметрами.
/// Новые ProcessInput можно добавлять в PropertyParameterDefinitions
/// без изменения CalcJob, PostgreSQL, Runtime.Service или Calc.Service.
/// </summary>
public sealed class CapacityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.capacity";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarAbsolute";
    private const string CorrectionKey = "capacityCorrection";

    private static readonly IReadOnlyList<CalculationParameterDefinition> PropertyParameterDefinitions =
    [
        // Temperature является настоящим процессным входом.
        //
        // Специализированный Capacity UI в будущем не должен искать
        // параметр по имени temperatureC.
        //
        // Он определит его по Role = ProcessInput точно так же,
        // как сейчас это делает DensityConfigurationPanel.
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

        // Pressure также является ProcessInput.
        //
        // Текущие перенесённые Capacity-корреляции его пока не используют,
        // поэтому параметр остаётся необязательным и имеет DefaultValue.
        //
        // Это важно: мы заранее сохраняем нормальный контракт Pressure,
        // но не заставляем Runtime требовать SCADA binding для формулы,
        // которой это давление сегодня математически не нужно.
        //
        // Если новая версия Capacity действительно начнёт зависеть
        // от Pressure, достаточно будет сделать IsRequired = true
        // и увеличить Version алгоритма.
        new CalculationParameterDefinition(
            Key: PressureKey,
            Name: "Absolute pressure",
            Type: CalculationParameterType.Number,
            Unit: "bar(abs)",
            IsRequired: false,
            DefaultValue: 1.01325d,
            Minimum: 0.000001,
            Step: 0.01,
            Decimals: 4,
            Order: 2,
            Description: "Reserved absolute pressure input. Current legacy Capacity correlations depend only on Temperature.",
            Role: CalculationParameterRole.ProcessInput),

        // Старый Capacity использовал DELTA_C.
        //
        // Mix.GetCapacity() уже возвращал значение в J/(kg·K).
        // DELTA_C после чтения из OPC умножался на 10,
        // а затем при расчёте использовался с коэффициентом 0.1.
        //
        // Эти два преобразования взаимно уничтожались.
        //
        // Поэтому в новой архитектуре correction сразу хранится
        // как инженерное значение J/(kg·K), без SCADA scaling
        // внутри математического ядра.
        new CalculationParameterDefinition(
            Key: CorrectionKey,
            Name: "Capacity correction",
            Type: CalculationParameterType.Number,
            Unit: "J/(kg·K)",
            IsRequired: false,
            DefaultValue: 0d,
            Step: 1,
            Decimals: 3,
            Order: 3,
            Description: "Engineering correction added to the calculated specific heat capacity. Corresponds to legacy DELTA_C.",
            Role: CalculationParameterRole.Configuration)
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateMixtureParameters(PropertyParameterDefinitions);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
            Key: "capacity",
            Name: "Specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 1,
            Description: "Calculated mixture specific heat capacity including Capacity correction.")
    ];

    public override string Code => DefinitionCode;

    public override string Name => "Mixture specific heat capacity";

    public override string Category => "Capacity";

    public override string Version => "1";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Выполняет расчёт Capacity.
    ///
    /// Pressure уже является частью общего контракта ProcessInput,
    /// но текущая перенесённая математическая модель Capacity
    /// использует только Temperature.
    ///
    /// Pressure всё равно читается из ParameterSet и добавляется в Trace.
    /// Благодаря этому Runtime State уже хранит полный текущий набор
    /// процессных параметров, даже если конкретная версия формулы
    /// использует только часть из них.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarAbsolute = parameters.GetRequiredDouble(PressureKey);
        var correctionJPerKgK = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);

        var baseCapacityJPerKgK = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(components, temperatureC);
        var capacityJPerKgK = baseCapacityJPerKgK + correctionJPerKgK;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
            return CalculationResult.Failure("capacity.result.invalid", "Calculated specific heat capacity after correction must be a finite value greater than zero.");

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem("temperatureC", "Temperature", Format(temperatureC), "°C"));
            trace.Add(new CalculationTraceItem("pressureBarAbsolute", "Absolute pressure", Format(pressureBarAbsolute), "bar(abs)"));

            AddMixtureTrace(trace, components);

            trace.Add(new CalculationTraceItem("baseCapacity", "Capacity before correction", Format(baseCapacityJPerKgK), "J/(kg·K)"));
            trace.Add(new CalculationTraceItem("capacityCorrection", "Capacity correction", Format(correctionJPerKgK), "J/(kg·K)"));
            trace.Add(new CalculationTraceItem("capacity", "Final specific heat capacity", Format(capacityJPerKgK), "J/(kg·K)"));
        }

        return CalculationResult.Success(
        [
            new CalculationOutput(
                Key: "capacity",
                Name: "Specific heat capacity",
                Value: capacityJPerKgK,
                Unit: "J/(kg·K)")
        ],
        trace: trace);
    }
}