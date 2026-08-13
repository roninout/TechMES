using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 7.
/// Горизонтальный сборник с двумя конусными боковинами.
///
/// Перенесён GetTypeSevenVolume().
/// Важно: коэффициент 0.33 оставлен именно таким, как в рабочем исходнике. Не заменяем его на 1/3.
/// </summary>
public sealed class TankType7VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12),
            Dimension("dimD", "dimD", 13)
        );

    public override string Code => "tank.volume.type7";

    public override string Name => "Type 7 — horizontal, two conical ends";

    public override IReadOnlyList<CalculationParameterDefinition>Parameters => ParameterDefinitions;

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
        var radius2 = dimD * 0.001 / 2.0;
        var levelFromSensorToBottomOfTheTank = Math.Max(0, dimB - distanceB + ltoDistanceA);

        double GetSomeVolume(double level, double length)
        {
            var alphaRadians = 2.0 * Math.Acos(Math.Max(1.0 - level / radius, -1.0));
            var alpha = alphaRadians * 180.0 / Math.PI;
            var s = 0.5 * radius * radius * (Math.PI * alpha / 180.0 - Math.Sin(alphaRadians));

            return s * length;
        }

        var volumeMainPart = GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001 + levelMm * 0.001, dimA * 0.001);
        var equivalentConicalLength = 0.33 * (dimC * 0.001) * (1.0 + ( radius2 * (radius + radius2) / Math.Pow(radius, 2))) * 2.0;
        var volumeOfConicalParts = GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001 + levelMm * 0.001, equivalentConicalLength);

        return volumeMainPart + volumeOfConicalParts;
    }
}