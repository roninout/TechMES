using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Tanks.Models;

namespace TechMES.Calc.Tanks;

/// <summary>
/// Представляет алгоритм расчёта объёма прямоугольного резервуара
/// в универсальном каталоге TechMES.Calc.
///
/// Метаданные Parameters в дальнейшем будут использоваться
/// для автоматического построения формы WEB-тестера.
/// </summary>
public sealed class RectangularTankVolumeDefinition : CalculationDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions =
        [
            new(
                Key: "levelMm",
                Name: "Measured level",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Step: 1,
                Decimals: 0,
                Order: 1,
                Description: "Level measured inside the configured sensor range."),

            new(
                Key: "heightMm",
                Name: "Tank height",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 1,
                Step: 1,
                Decimals: 0,
                Order: 2),

            new(
                Key: "widthMm",
                Name: "Tank width",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 1,
                Step: 1,
                Decimals: 0,
                Order: 3),

            new(
                Key: "lengthMm",
                Name: "Tank length",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 1,
                Step: 1,
                Decimals: 0,
                Order: 4),

            new(
                Key: "distanceToPointAMm",
                Name: "Distance to point A",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 0,
                Step: 1,
                Decimals: 0,
                Order: 5,
                Description: "Legacy DistToDistanceA value."),

            new(
                Key: "distanceAMm",
                Name: "Distance A",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 0,
                Step: 1,
                Decimals: 0,
                Order: 6),

            new(
                Key: "distanceBMm",
                Name: "Distance B",
                Type: CalculationParameterType.Number,
                Unit: "mm",
                Minimum: 0,
                Step: 1,
                Decimals: 0,
                Order: 7)
        ];

    private static readonly IReadOnlyList<CalculationOutputDefinition>
        OutputDefinitions =
        [
            new(
                Key: "volume",
                Name: "Volume",
                Unit: "m³",
                Decimals: 3,
                Order: 1,
                Description: "Calculated liquid volume.")
        ];

    public override string Code => "tank.volume.rectangular";

    public override string Name => "Rectangular tank volume";

    public override string Category => "Tanks";

    public override string Version => "1";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Создаёт типизированную модель и запускает чистый калькулятор.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var input = new RectangularTankVolumeInput(
            MeasuredLevelMm: parameters.GetRequiredDouble("levelMm"),
            Tank: new RectangularTankGeometry(
                HeightMm: parameters.GetRequiredDouble("heightMm"),
                WidthMm: parameters.GetRequiredDouble("widthMm"),
                LengthMm: parameters.GetRequiredDouble("lengthMm")),
            Measurement: new TankLevelMeasurementGeometry(
                DistanceToPointAMm:
                    parameters.GetRequiredDouble("distanceToPointAMm"),
                DistanceAMm:
                    parameters.GetRequiredDouble("distanceAMm"),
                DistanceBMm:
                    parameters.GetRequiredDouble("distanceBMm")));

        var calculation = RectangularTankVolumeCalculator.Calculate(input);

        var messages = BuildMessages(input, calculation);

        var trace = includeTrace ? BuildTrace(input, calculation) : Array.Empty<CalculationTraceItem>();

        return CalculationResult.Success(
        [
            new CalculationOutput(
                Key: "volume",
                Name: "Volume",
                Value: calculation.VolumeM3,
                Unit: "m³")
        ],
        messages,
        trace);
    }

    /// <summary>
    /// Создаёт предупреждения без изменения совместимого результата.
    /// </summary>
    private static IReadOnlyList<CalculationMessage> BuildMessages(RectangularTankVolumeInput input, RectangularTankVolumeCalculation calculation)
    {
        var messages = new List<CalculationMessage>();

        if (input.MeasuredLevelMm < 0)
        {
            messages.Add(new CalculationMessage(
                Code: "tank.level-below-zero",
                Message: "Measured level is below zero and was normalized to zero.",
                Severity: CalculationMessageSeverity.Warning));
        }

        // Старый алгоритм не ограничивал объём полной высотой.
        // Пока сохраняем это поведение, но явно сообщаем о превышении.
        if (calculation.EffectiveLiquidHeightMm > input.Tank.HeightMm)
        {
            messages.Add(new CalculationMessage(
                Code: "tank.level-above-height",
                Message: "Effective liquid height exceeds the configured tank height.",
                Severity: CalculationMessageSeverity.Warning));
        }

        return messages;
    }

    /// <summary>
    /// Формирует подробные промежуточные значения для WEB-тестера.
    /// </summary>
    private static IReadOnlyList<CalculationTraceItem> BuildTrace(RectangularTankVolumeInput input, RectangularTankVolumeCalculation calculation)
    {
        return
        [
            Trace(
                "measuredLevelInputMm",
                "Measured level input",
                input.MeasuredLevelMm,
                "mm"),

            Trace(
                "normalizedMeasuredLevelMm",
                "Normalized measured level",
                calculation.NormalizedMeasuredLevelMm,
                "mm"),

            Trace(
                "measurementSpanMm",
                "Measurement span",
                calculation.MeasurementSpanMm,
                "mm"),

            Trace(
                "unmeasuredBottomHeightMm",
                "Unmeasured bottom height",
                calculation.UnmeasuredBottomHeightMm,
                "mm"),

            Trace(
                "effectiveLiquidHeightMm",
                "Effective liquid height",
                calculation.EffectiveLiquidHeightMm,
                "mm"),

            Trace(
                "crossSectionAreaM2",
                "Cross-section area",
                calculation.CrossSectionAreaM2,
                "m²"),

            Trace(
                "volumeM3",
                "Calculated volume",
                calculation.VolumeM3,
                "m³")
        ];
    }

    /// <summary>
    /// Создаёт диагностический элемент с независимым от локали форматом.
    /// </summary>
    private static CalculationTraceItem Trace(string key, string name, double value, string unit)
    {
        return new CalculationTraceItem(
            Key: key,
            Name: name,
            Value: value.ToString("0.############", CultureInfo.InvariantCulture),
            Unit: unit);
    }
}