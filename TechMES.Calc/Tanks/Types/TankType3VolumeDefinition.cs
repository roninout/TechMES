using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 3.
/// Вертикальный сборник с перегородкой.
///
/// Используются:
/// dimA
/// dimB
/// dimC
/// dimD
/// distanceB
/// distToDistanceA
/// levelMm
/// </summary>
public sealed class TankType3VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12),
            Dimension("dimD", "dimD", 13)
        );

    public override string Code => "tank.volume.type3";

    public override string Name => "Type 3 — vertical with partition";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var levelMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");
        var distanceB = parameters.GetRequiredDouble("distanceB");
        var ltoDistanceA = parameters.GetRequiredDouble("distToDistanceA");
        var radius = dimB * 0.001 / 2.0;
        var totalLength = dimA + dimC;
        var levelFromSensorToBottomOfTheTank = Math.Max(0, totalLength - distanceB + ltoDistanceA);

        double GetSomeVolume(double level)
        {
            var alphaRadians = 2.0 * Math.Acos(Math.Max((dimD * 0.001 - radius) / radius, -1.0));
            var alpha = 360.0 - alphaRadians * 180.0 / Math.PI;
            var s = 0.5 * radius * radius * (Math.PI * alpha / 180.0 - Math.Sin(2.0 * Math.PI - alphaRadians));

            return s * level;
        }

        var volumeLeft = GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001) * 0.85;
        var volumeLevel = GetSomeVolume(levelMm * 0.001);

        return volumeLeft + volumeLevel;
    }
}