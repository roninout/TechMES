using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TANK TYPE 3.
///
/// Вертикальный узкий резервуар с нижним выпуклым днищем
/// и боковой измерительной / присоединительной частью.
///
/// По рабочему чертежу используются:
///
/// dimA
/// dimB
/// dimC
/// dimD
///
/// Пока сохраняем эти стабильные имена без попытки
/// искусственно переименовать размеры.
///
/// Точный физический смысл dimD и piecewise-формула
/// должны быть подтверждены legacy Tank.cs.
/// </summary>
public sealed class TankType3VolumeDefinition
    : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions =
        CreateParameters(
            Dimension(
                key: "dimA",
                name: "dimA",
                order: 10),

            Dimension(
                key: "dimB",
                name: "dimB",
                order: 11),

            Dimension(
                key: "dimC",
                name: "dimC",
                order: 12),

            Dimension(
                key: "dimD",
                name: "dimD",
                order: 13)
        );

    public override string Code => "tank.volume.type3";

    public override string Name =>
        "Type 3 — vertical vessel with side insertion";

    public override IReadOnlyList<CalculationParameterDefinition>
        Parameters =>
        ParameterDefinitions;
}