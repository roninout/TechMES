using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Density;

/// <summary>
/// Расчёт плотности многокомпонентной смеси.
///
/// Текущая версия использует два физических параметра:
/// - Temperature, °C;
/// - absolute Pressure, bar(abs).
///
/// Сам Calculation Engine не ограничен этими двумя параметрами.
/// Если в будущем новая формула Density потребует дополнительные значения,
/// они просто добавляются в PropertyParameterDefinitions.
///
/// Например:
/// - concentration;
/// - humidity;
/// - compressibility;
///
/// После изменения математического контракта увеличивается Version,
/// но CalcJob, PostgreSQL, Runtime.Service и Calc.Service менять не потребуется.
/// </summary>
public sealed class DensityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.density";
    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarAbsolute";
    private const string CorrectionKey = "densityCorrection";

    /// <summary>
    /// Параметры именно физической модели Density.
    ///
    /// Этот список намеренно находится отдельно от параметров состава смеси.
    /// Благодаря этому количество физических входов может свободно расширяться.
    ///
    /// Сегодня здесь три значения:
    /// - Temperature;
    /// - Pressure;
    /// - correction.
    ///
    /// Correction не является самостоятельным физическим состоянием процесса.
    /// Это эксплуатационная поправка, эквивалент старого DELTA_D.
    /// </summary>
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
            Description: "Mixture temperature used by the substance density correlations."),

        new CalculationParameterDefinition(
            Key: PressureKey,
            Name: "Absolute pressure",
            Type: CalculationParameterType.Number,
            Unit: "bar(abs)",
            IsRequired: true,
            Minimum: 0.000001,
            Step: 0.01,
            Decimals: 4,
            Order: 2,
            Description: "Absolute mixture pressure. Gauge pressure must not be passed here without atmospheric-pressure compensation."),

        /*
         * В старом TechParamsCalc Density рассчитывалась так:
         *
         * Mix.GetDensity() -> физическая Density * 10
         * DELTA_D из OPC -> * 100
         * ValCalc = DensityRaw + DeltaD * 0.1
         *
         * После удаления SCADA scaling это эквивалентно:
         *
         * DensityEngineering = Density + DELTA_D
         *
         * Поэтому в новой модели correction сразу хранится в kg/m³.
         */
        new CalculationParameterDefinition(
            Key: CorrectionKey,
            Name: "Density correction",
            Type: CalculationParameterType.Number,
            Unit: "kg/m³",
            IsRequired: false,
            DefaultValue: 0d,
            Step: 0.1,
            Decimals: 3,
            Order: 3,
            Description: "Engineering correction added to the calculated density. Corresponds to legacy DELTA_D without SCADA scaling.")
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateMixtureParameters(PropertyParameterDefinitions);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
            Key: "density",
            Name: "Density",
            Unit: "kg/m³",
            Decimals: 3,
            Order: 1,
            Description: "Calculated mixture density including Density correction.")
    ];

    public override string Code => DefinitionCode;

    public override string Name => "Mixture density";

    public override string Category => "Density";

    public override string Version => "1";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Выполняет физический расчёт Density.
    ///
    /// К этому моменту CalculationDefinitionBase уже:
    /// - применил DefaultValue;
    /// - проверил типы;
    /// - проверил диапазоны;
    /// - отклонил неизвестные параметры.
    ///
    /// Здесь остаётся только специализированная логика Density:
    /// 1. читаем физические параметры;
    /// 2. формируем фактический состав смеси;
    /// 3. рассчитываем базовую Density;
    /// 4. добавляем эксплуатационную correction;
    /// 5. формируем результат и Trace.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarAbsolute = parameters.GetRequiredDouble(PressureKey);
        var correctionKgPerM3 = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);

        var baseDensityKgPerM3 = MixturePropertyCalculator.CalculateDensityKgPerM3(components, temperatureC, pressureBarAbsolute);
        var densityKgPerM3 = baseDensityKgPerM3 + correctionKgPerM3;

        if (!double.IsFinite(densityKgPerM3) || densityKgPerM3 <= 0d)
            return CalculationResult.Failure("density.result.invalid", "Calculated density after correction must be a finite value greater than zero.");

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem(
                "temperatureC",
                "Temperature",
                Format(temperatureC),
                "°C"));

            trace.Add(new CalculationTraceItem(
                "pressureBarAbsolute",
                "Absolute pressure",
                Format(pressureBarAbsolute),
                "bar(abs)"));

            AddMixtureTrace(trace, components);

            trace.Add(new CalculationTraceItem(
                "baseDensity",
                "Density before correction",
                Format(baseDensityKgPerM3),
                "kg/m³"));

            trace.Add(new CalculationTraceItem(
                "densityCorrection",
                "Density correction",
                Format(correctionKgPerM3),
                "kg/m³"));

            trace.Add(new CalculationTraceItem(
                "density",
                "Final density",
                Format(densityKgPerM3),
                "kg/m³"));
        }

        return CalculationResult.Success(
        [
            new CalculationOutput(
                Key: "density",
                Name: "Density",
                Value: densityKgPerM3,
                Unit: "kg/m³")
        ],
        trace: trace);
    }
}