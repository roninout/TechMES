using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TANK TYPE 2.
///
/// Горизонтальный цилиндрический резервуар
/// с выпуклыми торцами.
///
/// По предоставленному чертежу:
///
/// dimA - длина основной цилиндрической части;
/// dimB - диаметр резервуара;
/// dimC - геометрическое смещение измерительной части.
///
/// Для частично заполненного горизонтального цилиндра
/// объём должен рассчитываться через площадь кругового сегмента.
///
/// Точную legacy-формулу и учёт выпуклых торцов
/// перенесём после сверки со старым Tank.cs.
/// </summary>
public sealed class TankType2VolumeDefinition
    : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions =
        CreateParameters(
            Dimension(
                key: "dimA",
                name: "dimA — cylindrical length",
                order: 10,
                description:
                    "Length of the straight cylindrical part."),

            Dimension(
                key: "dimB",
                name: "dimB — tank diameter",
                order: 11,
                description:
                    "Inside tank diameter."),

            Dimension(
                key: "dimC",
                name: "dimC — measurement offset",
                order: 12,
                description:
                    "Measurement/insertion geometry offset.")
        );

    public override string Code => "tank.volume.type2";

    public override string Name =>
        "Type 2 — horizontal cylindrical, dished ends";

    public override IReadOnlyList<CalculationParameterDefinition>Parameters => ParameterDefinitions;
}