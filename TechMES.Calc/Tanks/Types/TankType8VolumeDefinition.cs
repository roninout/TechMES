using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 8.
/// Вертикальный сборник с усечённым цилиндром.
///
/// Перенесён GetTypeEighthVolume().
///
/// По структуре близок TYPE 5, но нижний неучтённый объём умножается на 1/3.
/// </summary>
public sealed class TankType8VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12),
            Dimension("dimD", "dimD", 13),
            Dimension("dimE", "dimE", 14),
            Dimension(key: "dimF", name: "dimF", order: 15, unit: "L", step: 0.1, decimals: 1)
        );

    public override string Code => "tank.volume.type8";

    public override string Name => "Type 8 — vertical with truncated cylinder";

    public override IReadOnlyList<CalculationParameterDefinition>Parameters => ParameterDefinitions;

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var levelMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");
        var dimE = parameters.GetRequiredDouble("dimE");
        var dimF = parameters.GetRequiredDouble("dimF");
        var distanceB = parameters.GetRequiredDouble("distanceB");
        var ltoDistanceA = parameters.GetRequiredDouble("distToDistanceA");
        var radius = dimB * 0.001 / 2.0;
        var totalLength = dimA + dimC;
        var levelFromSensorToBottomOfTheTank = Math.Max(0, totalLength - distanceB + ltoDistanceA);

        double GetSomeVolume(double level)
        {
            var s = radius * radius * Math.PI;
            return s * level;
        }

        double volumeTotal = 0;

        // Старый алгоритм: 1/3 объёма цилиндра для усечённой части.
        volumeTotal += GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001) / 3.0;
        var distFromDistBToLowRorvand = Math.Max(0, distanceB - dimD - dimE);

        if (levelMm <= distFromDistBToLowRorvand)
        {
            volumeTotal += GetSomeVolume(levelMm * 0.001);
            return volumeTotal;
        }

        volumeTotal += GetSomeVolume(distFromDistBToLowRorvand * 0.001);

        var distFromDistBToHighRorwand = Math.Max(0, distFromDistBToLowRorvand + dimD);
        var volumeOfReboilerWithoutTubes = Math.Max(0.0, dimF * 0.001);

        if (levelMm > distFromDistBToLowRorvand && levelMm <= distFromDistBToHighRorwand)
        {
            volumeTotal += volumeOfReboilerWithoutTubes * (levelMm - distFromDistBToLowRorvand) / dimD;
            return volumeTotal;
        }

        volumeTotal += volumeOfReboilerWithoutTubes;
        var distFromHighRorwand = levelMm - distFromDistBToLowRorvand - dimD;
        var volumeOfTopOfTank = GetSomeVolume(distFromHighRorwand * 0.001);
        volumeTotal += volumeOfTopOfTank;
        return volumeTotal;
    }
}