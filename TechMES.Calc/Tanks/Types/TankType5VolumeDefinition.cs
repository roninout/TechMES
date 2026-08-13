using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 5.
/// Куб колонны со встроенным ребойлером.
///
/// Используются:
/// dimA..dimF
/// distanceB
/// distToDistanceA
/// levelMm
///
/// ВАЖНО:
/// исходный код использует dimF * 0.001 непосредственно
/// как полезный объём участка ребойлера.
///
/// Хотя описание исходных данных говорит,
/// что dimF является объёмом трубок ребойлера.
///
/// Пока НЕ исправляем это поведение.
/// Сначала переносим алгоритм 1:1.
/// </summary>
public sealed class TankType5VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12),
            Dimension("dimD", "dimD", 13),
            Dimension("dimE", "dimE", 14),

            Dimension(
                key: "dimF",
                name: "dimF — reboiler tube volume",
                order: 15,
                unit: "L",
                step: 0.1,
                decimals: 1)
        );

    public override string Code => "tank.volume.type5";

    public override string Name => "Type 5 — column with internal reboiler";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

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
        var levelFromSensorToBottomOfTheTank =Math.Max(0, totalLength - distanceB + ltoDistanceA);

        double GetSomeVolume(double level)
        {
            var s = radius * radius * Math.PI;
            return s * level;
        }

        double volumeTotal = 0;
        volumeTotal += GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001) * 0.85;
        var distFromDistBToLowRorvand = Math.Max(0, distanceB - dimD - dimE);

        if (levelMm <= distFromDistBToLowRorvand)
        {
            volumeTotal += GetSomeVolume(levelMm * 0.001);
            return volumeTotal;
        }

        volumeTotal += GetSomeVolume(distFromDistBToLowRorvand * 0.001);
        var distFromDistBToHighRorwand = Math.Max(0, distFromDistBToLowRorvand + dimD);

         // Переносим именно рабочую строку старого кода:
         // Math.Max(0.0, dimF * 0.001)
         // Семантику dimF отдельно проверим позже.
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