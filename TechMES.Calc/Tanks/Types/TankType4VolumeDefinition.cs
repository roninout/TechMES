using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 4.
/// Сборник-параллелепипед.
///
/// Используются:
/// dimA
/// dimB
/// dimC
/// distanceA
/// distanceB
/// distToDistanceA
/// levelMm
/// </summary>
public sealed class TankType4VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12)
        );

    public override string Code => "tank.volume.type4";

    public override string Name => "Type 4 — rectangular tank";

    public override IReadOnlyList<CalculationParameterDefinition>Parameters => ParameterDefinitions;

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var levelMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var distanceA = parameters.GetRequiredDouble("distanceA");
        var distanceB = parameters.GetRequiredDouble("distanceB");
        var ltoDistanceA = parameters.GetRequiredDouble("distToDistanceA");
        var totalLength = dimA;

        // ВАЖНО:
        // TYPE 4 в рабочем Tank.cs использует именно:
        //
        // totalLength - (ltoDistanceA + (distanceB - distanceA))
        // Оставляем эту формулу без изменения.
        var levelFromSensorToBottomOfTheTank = Math.Max(0, totalLength - (ltoDistanceA + (distanceB - distanceA)));
        var volumeLeft = dimB * 0.001 * dimC * 0.001 * levelFromSensorToBottomOfTheTank * 0.001;
        var volumeLevel = dimB * 0.001 * dimC * 0.001 * Math.Max(0, levelMm) * 0.001;

        return volumeLevel + Math.Max(0, volumeLeft);
    }
}