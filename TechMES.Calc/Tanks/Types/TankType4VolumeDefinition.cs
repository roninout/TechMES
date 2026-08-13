using System.Globalization;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Tanks.Models;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TANK TYPE 4.
///
/// Прямоугольный резервуар.
///
/// По предоставленному чертежу:
///
/// dimA - высота резервуара;
/// dimB - ширина;
/// dimC - глубина / длина.
///
/// Для этого Tank Type уже существует проверенная
/// legacy-compatible реализация:
///
/// RectangularTankVolumeCalculator
///
/// Поэтому здесь не дублируем формулу,
/// а адаптируем новый Tank Type к существующему калькулятору.
/// </summary>
public sealed class TankType4VolumeDefinition
    : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions =
        CreateParameters(
            Dimension(
                key: "dimA",
                name: "dimA — tank height",
                order: 10,
                description:
                    "Inside tank height."),

            Dimension(
                key: "dimB",
                name: "dimB — tank width",
                order: 11,
                description:
                    "Inside tank width."),

            Dimension(
                key: "dimC",
                name: "dimC — tank depth",
                order: 12,
                description:
                    "Inside tank depth / length.")
        );

    public override string Code => "tank.volume.type4";

    public override string Name =>
        "Type 4 — rectangular tank";

    public override IReadOnlyList<CalculationParameterDefinition>
        Parameters =>
        ParameterDefinitions;

    /// <summary>
    /// TYPE 4 использует существующий проверенный
    /// RectangularTankVolumeCalculator.
    /// </summary>
    protected override CalculationResult CalculateCore(
        CalculationParameterSet parameters,
        bool includeTrace)
    {
        /*
         * Преобразуем новые общие Tank-параметры
         * в существующую типизированную модель.
         */
        var input = new RectangularTankVolumeInput(
            MeasuredLevelMm:
                parameters.GetRequiredDouble("levelMm"),

            Tank:
                new RectangularTankGeometry(
                    HeightMm:
                        parameters.GetRequiredDouble("dimA"),

                    WidthMm:
                        parameters.GetRequiredDouble("dimB"),

                    LengthMm:
                        parameters.GetRequiredDouble("dimC")),

            Measurement:
                new TankLevelMeasurementGeometry(
                    DistanceToPointAMm:
                        parameters.GetRequiredDouble(
                            "distToDistanceA"),

                    DistanceAMm:
                        parameters.GetRequiredDouble(
                            "distanceA"),

                    DistanceBMm:
                        parameters.GetRequiredDouble(
                            "distanceB"))
        );

        /*
         * Используем существующую рабочую формулу.
         */
        var calculation =
            RectangularTankVolumeCalculator.Calculate(input);

        IReadOnlyList<CalculationTraceItem> trace =
            includeTrace
                ?
                [
                    new CalculationTraceItem(
                        Key: "measurementSpanMm",
                        Name: "Measurement span",
                        Value:
                            calculation.MeasurementSpanMm
                                .ToString(
                                    "0.############",
                                    CultureInfo.InvariantCulture),
                        Unit: "mm"),

                    new CalculationTraceItem(
                        Key: "unmeasuredBottomHeightMm",
                        Name: "Unmeasured bottom height",
                        Value:
                            calculation.UnmeasuredBottomHeightMm
                                .ToString(
                                    "0.############",
                                    CultureInfo.InvariantCulture),
                        Unit: "mm"),

                    new CalculationTraceItem(
                        Key: "effectiveLiquidHeightMm",
                        Name: "Effective liquid height",
                        Value:
                            calculation.EffectiveLiquidHeightMm
                                .ToString(
                                    "0.############",
                                    CultureInfo.InvariantCulture),
                        Unit: "mm")
                ]
                :
                [];

        return CalculationResult.Success(
            outputs:
            [
                new CalculationOutput(
                    Key: "volume",
                    Name: "Volume",
                    Value: calculation.VolumeM3,
                    Unit: "m³")
            ],
            trace: trace);
    }
}