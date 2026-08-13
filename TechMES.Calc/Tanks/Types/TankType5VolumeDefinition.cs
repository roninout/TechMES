using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TANK TYPE 5.
///
/// Вертикальный ребойлер / аппарат с трубным пучком.
///
/// По предоставленному чертежу:
///
/// dimA
/// dimB
/// dimC
/// dimD
/// dimE
///
/// dimF - объём трубок ребойлера в ЛИТРАХ.
///
/// dimF принципиально отличается от остальных размеров:
/// это не линейный размер, а внутренний вытесненный объём.
///
/// При окончательном расчёте этот объём должен учитываться
/// согласно старому Tank-алгоритму.
/// </summary>
public sealed class TankType5VolumeDefinition
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
                order: 13),

            Dimension(
                key: "dimE",
                name: "dimE",
                order: 14),

            Dimension(
                key: "dimF",
                name: "dimF — reboiler tube volume",
                order: 15,
                description:
                    "Internal volume of reboiler tubes.",
                unit: "L",
                defaultValue: 0d,
                minimum: 0d,
                step: 0.1d,
                decimals: 1)
        );

    public override string Code => "tank.volume.type5";

    public override string Name =>
        "Type 5 — vertical reboiler";

    public override IReadOnlyList<CalculationParameterDefinition>
        Parameters =>
        ParameterDefinitions;
}