using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TANK TYPE 1.
///
/// Вертикальный цилиндрический резервуар
/// с выпуклым верхним и нижним днищем.
///
/// По предоставленному чертежу:
///
/// dimA - высота основной цилиндрической части;
/// dimB - диаметр резервуара;
/// dimC - высота выпуклого днища.
///
/// Положение датчика и его измерительный диапазон
/// задаются общими параметрами Tank.
///
/// Точная формула объёма выпуклых частей будет перенесена
/// после сверки со старым Tank.cs.
/// </summary>
public sealed class TankType1VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions =
        CreateParameters(
            Dimension(
                key: "dimA",
                name: "dimA — cylindrical height",
                order: 10,
                description:
                    "Height of the straight cylindrical part."),

            Dimension(
                key: "dimB",
                name: "dimB — tank diameter",
                order: 11,
                description:
                    "Inside tank diameter."),

            Dimension(
                key: "dimC",
                name: "dimC — dished head height",
                order: 12,
                description:
                    "Height of the dished tank head.")
        );

    /// <summary>
    /// Стабильный код алгоритма.
    /// Именно он будет сохраняться в CalcJob.DefinitionCode.
    /// </summary>
    public override string Code => "tank.volume.type1";

    public override string Name =>
        "Type 1 — vertical cylindrical, dished ends";

    public override IReadOnlyList<CalculationParameterDefinition>
        Parameters =>
        ParameterDefinitions;
}