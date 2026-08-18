using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 6.
/// Вертикальный сборник с двумя конусными днищами.
///
/// Перенесён GetTypeSixVolume().
/// </summary>
public sealed class TankType6VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12)
        );

    public override string Code => "tank.volume.type6";

    public override string Name => "Type 6 — vertical, two conical heads";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimA") + parameters.GetRequiredDouble("dimC") * 2.0;
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var levelMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var distanceB = parameters.GetRequiredDouble("distanceB");
        var ltoDistanceA = parameters.GetRequiredDouble("distToDistanceA");
        var radius = dimB * 0.001 / 2.0;
        var totalLength = dimA + dimC * 2.0;
        var levelFromSensorToBottomOfTheTank = Math.Max(0, totalLength - distanceB + ltoDistanceA);
        var volumeLeft = Math.PI * radius * radius * levelFromSensorToBottomOfTheTank * 0.001 * 0.8;
        var volumeLevel = Math.PI * radius * radius * Math.Max(0, levelMm) * 0.001;

        return volumeLevel + Math.Max(0, volumeLeft);
    }
}