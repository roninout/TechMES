using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Density;

/// <summary>
/// Расчёт плотности многокомпонентной смеси.
///
/// Физические ProcessInput:
/// - Temperature;
/// - absolute Pressure;
/// - в будущем могут быть добавлены другие параметры.
///
/// Количество ProcessInput Calculation Engine не ограничивает.
///
/// Состав смеси в рабочем Density Job формируется из двух источников:
///
/// SCADA:
/// - CompN;
/// - Perc0...Perc4.
///
/// Конфигурация TechMES:
/// - component0Code...component4Code.
///
/// Таким образом SCADA определяет количество компонентов и их текущие
/// массовые проценты, а TechMES определяет, какое вещество соответствует
/// каждому Perc-слоту.
///
/// DeltaD также является read-only SCADA input.
///
/// Единственный расчётный output:
/// density -> Density.ValCalc.
/// </summary>
public sealed class DensityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.density";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarAbsolute";
    private const string CorrectionKey = "densityCorrection";

    /// <summary>
    /// Параметры физической модели Density.
    ///
    /// Role = ProcessInput используется только для внешних процессных
    /// параметров, которые пользователь связывает через WEB.
    ///
    /// CompN/Perc являются другой частью контракта смеси и создаются
    /// MixtureCalculationDefinitionBase.
    /// </summary>
    private static readonly IReadOnlyList<CalculationParameterDefinition> PropertyParameterDefinitions =
    [
        // Первый штатный процессный параметр Density.
        new CalculationParameterDefinition(
        Key: TemperatureKey, Name: "Temperature", Type: CalculationParameterType.Number, Unit: "°C",
        IsRequired: true, Minimum: -273.15, Step: 0.1, Decimals: 2, Order: 1,
        Description: "Mixture temperature used by the substance density correlations.",
        Role: CalculationParameterRole.ProcessInput),

    // Второй штатный процессный параметр.
    //
    // В UI показываем просто Pressure.
    // Внутренний ключ pressureBarAbsolute пока оставляем,
    // потому что он описывает физический контракт текущей формулы.
    new CalculationParameterDefinition(
        Key: PressureKey, Name: "Pressure", Type: CalculationParameterType.Number, Unit: "bar(abs)",
        IsRequired: true, Minimum: 0.000001, Step: 0.01, Decimals: 4, Order: 2,
        Description: "Absolute mixture pressure.",
        Role: CalculationParameterRole.ProcessInput),

    // Три резервных ProcessInput закладываем уже сейчас.
    //
    // Текущая формула Density их не использует.
    // Они нужны для того, чтобы специализированный UI и Calc Job
    // уже поддерживали расширение физической модели до пяти параметров
    // без изменения Runtime, PostgreSQL и структуры Job.
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

    // DeltaD оператор в TechMES не редактирует.
    // В рабочем Job он автоматически связан с ITEM DeltaD самого Density Equipment.
    new CalculationParameterDefinition(
        Key: CorrectionKey, Name: "DeltaD", Type: CalculationParameterType.Number, Unit: "kg/m³",
        IsRequired: false, DefaultValue: 0d, Step: 0.1, Decimals: 3, Order: 10,
        Description: "Density correction read from the Density Equipment DeltaD SCADA item.",
        Role: CalculationParameterRole.Configuration)
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateMixtureParameters(PropertyParameterDefinitions);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
            Key: "density", Name: "Density", Unit: "kg/m³", Decimals: 3, Order: 1,
            Description: "Calculated mixture density including SCADA DeltaD.")
    ];

    public override string Code => DefinitionCode;
    public override string Name => "Mixture density";
    public override string Category => "Density";

    // Version 2 фиксирует новый runtime-контракт: CompN/Perc/DeltaD теперь поступают из SCADA, а не задаются оператором как Constants.
    public override string Version => "2";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Выполняет физический расчёт Density.
    ///
    /// К этому моменту Calc Service уже прочитал:
    /// - Temperature;
    /// - Pressure;
    /// - CompN;
    /// - Perc0...Perc4;
    /// - DeltaD;
    ///
    /// А componentNCode были получены из сохранённой конфигурации Job.
    ///
    /// ReadMixtureComponents() использует CompN и только первые активные
    /// componentNCode/componentNPercent.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarAbsolute = parameters.GetRequiredDouble(PressureKey);
        var deltaD = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);

        var baseDensityKgPerM3 = MixturePropertyCalculator.CalculateDensityKgPerM3(components, temperatureC, pressureBarAbsolute);
        var densityKgPerM3 = baseDensityKgPerM3 + deltaD;

        if (!double.IsFinite(densityKgPerM3) || densityKgPerM3 <= 0d)
            return CalculationResult.Failure("density.result.invalid", "Calculated density after DeltaD must be a finite value greater than zero.");

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem("temperatureC", "Temperature", Format(temperatureC), "°C"));
            trace.Add(new CalculationTraceItem("pressureBarAbsolute", "Absolute pressure", Format(pressureBarAbsolute), "bar(abs)"));

            AddMixtureTrace(trace, components);

            trace.Add(new CalculationTraceItem("baseDensity", "Density before DeltaD", Format(baseDensityKgPerM3), "kg/m³"));
            trace.Add(new CalculationTraceItem("densityCorrection", "DeltaD", Format(deltaD), "kg/m³"));
            trace.Add(new CalculationTraceItem("density", "Final density", Format(densityKgPerM3), "kg/m³"));
        }

        return CalculationResult.Success(
        [
            new CalculationOutput(Key: "density", Name: "Density", Value: densityKgPerM3, Unit: "kg/m³")
        ],
        trace: trace);
    }
}