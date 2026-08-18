using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 2.
/// Горизонтальный сборник с двумя эллиптическими боковинами.
///
/// Используются:
/// dimA
/// dimB
/// dimC
/// distanceB
/// distToDistanceA
/// levelMm
/// </summary>
public sealed class TankType2VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10),
            Dimension("dimB", "dimB", 11),
            Dimension("dimC", "dimC", 12)
        );

    public override string Code => "tank.volume.type2";

    public override string Name => "Type 2 — horizontal, two elliptical ends";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimB");
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var levelMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var distanceB = parameters.GetRequiredDouble("distanceB");
        var ltoDistanceA = parameters.GetRequiredDouble("distToDistanceA");
        var radius =dimB * 0.001 / 2.0;

        var levelFromSensorToBottomOfTheTank = Math.Max(0, dimB - distanceB + ltoDistanceA);

        double GetSomeVolume(double level, double length)
        {
            var alphaRadians = 2.0 * Math.Acos(Math.Max(1.0 - level / radius, -1.0));
            var alpha = alphaRadians * 180.0 / Math.PI;
            var s = 0.5 * radius * radius * (Math.PI * alpha / 180.0 - Math.Sin(alphaRadians));

            return s * length;
        }

        // Основная цилиндрическая часть.
        var volumeMainPart = GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001 + levelMm * 0.001, dimA * 0.001);
        
        // Эквивалентная длина двух эллиптических частей. 0.681 — существующий коэффициент.
        var volumeOfEllipticParts = GetSomeVolume(levelFromSensorToBottomOfTheTank * 0.001 + levelMm * 0.001, dimC * 2.0 * 0.001 * 0.681);

        return volumeMainPart + volumeOfEllipticParts;
    }
}