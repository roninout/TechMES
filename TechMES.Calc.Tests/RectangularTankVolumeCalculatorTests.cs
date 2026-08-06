using TechMES.Calc.Exceptions;
using TechMES.Calc.Tanks;
using TechMES.Calc.Tanks.Legacy;
using TechMES.Calc.Tanks.Models;

namespace TechMES.Calc.Tests;

/// <summary>
/// Сравнивает новую реализацию с точной старой формулой Tank Type 4.
/// </summary>
public sealed class RectangularTankVolumeCalculatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(-200)]
    [InlineData(4000)]
    public void MatchesLegacyTypeFourCalculation(int levelMm)
    {
        var legacyVolume =
            LegacyRectangularTankVolumeCalculator.Calculate(
                levelMm: levelMm,
                heightMm: 4000,
                widthMm: 2000,
                lengthMm: 3000,
                distanceToPointAMm: 500,
                distanceAMm: 100,
                distanceBMm: 3100);

        var calculation =
            RectangularTankVolumeCalculator.Calculate(
                CreateInput(levelMm));

        Assert.Equal(
            legacyVolume,
            calculation.VolumeM3,
            precision: 10);
    }

    [Fact]
    public void CalculatesExpectedIntermediateValues()
    {
        var calculation =
            RectangularTankVolumeCalculator.Calculate(
                CreateInput(measuredLevelMm: 1500));

        Assert.Equal(6.0, calculation.CrossSectionAreaM2, 10);
        Assert.Equal(3000.0, calculation.MeasurementSpanMm, 10);
        Assert.Equal(500.0, calculation.UnmeasuredBottomHeightMm, 10);
        Assert.Equal(2000.0, calculation.EffectiveLiquidHeightMm, 10);
        Assert.Equal(12.0, calculation.VolumeM3, 10);
    }

    [Fact]
    public void RejectsInvalidMeasurementOrder()
    {
        var input = new RectangularTankVolumeInput(
            MeasuredLevelMm: 1000,
            Tank: new RectangularTankGeometry(
                HeightMm: 4000,
                WidthMm: 2000,
                LengthMm: 3000),
            Measurement: new TankLevelMeasurementGeometry(
                DistanceToPointAMm: 1500,
                DistanceAMm: 1000,
                DistanceBMm: 500));

        var exception = Assert.Throws<CalculationException>(
            () => RectangularTankVolumeCalculator.Calculate(input));

        Assert.Equal(
            "tank.measurement.distance-order-invalid",
            exception.Code);
    }

    /// <summary>
    /// Создаёт стандартный корректный набор входов для тестов.
    /// </summary>
    private static RectangularTankVolumeInput CreateInput(double measuredLevelMm)
    {
        return new RectangularTankVolumeInput(
            MeasuredLevelMm: measuredLevelMm,
            Tank: new RectangularTankGeometry(
                HeightMm: 4000,
                WidthMm: 2000,
                LengthMm: 3000),
            Measurement: new TankLevelMeasurementGeometry(
                DistanceToPointAMm: 500,
                DistanceAMm: 100,
                DistanceBMm: 3100));
    }
}