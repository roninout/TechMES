using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 2.
///
/// Горизонтальный Tank:
///
/// левое полуэллипсоидное днище
/// +
/// горизонтальная цилиндрическая часть
/// +
/// правое полуэллипсоидное днище.
///
/// dimA - длина цилиндрической части, mm.
/// dimB - внутренний диаметр Tank, mm.
/// dimC - осевая глубина одного эллиптического днища, mm.
///
/// Уровень измеряется вертикально от нижней точки Tank.
///
/// Все формулы геометрии, промежуточные объёмы
/// и Help для Type 2 находятся в этом классе.
/// </summary>
public sealed class TankType2VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions =
        CreateParameters(
            Dimension("dimA", "dimA", 10, minimum: 1d),
            Dimension("dimB", "dimB", 11, minimum: 1d),
            Dimension("dimC", "dimC", 12));

    public override string Code => "tank.volume.type2";
    public override string Name => "Type 2 — horizontal, two elliptical ends";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Для горизонтального Tank полный размер по направлению
    // измерения уровня равен внутреннему диаметру.
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimB");
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        return CalculateDetails(parameters, liquidHeightMm).VolumeM3;
    }

    /// <summary>
    /// Help Type 2.
    ///
    /// Здесь намеренно нет старого коэффициента 0.681.
    /// Объём двух эллиптических торцов рассчитывается непосредственно по геометрии эллипсоида.
    /// </summary>
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Geometry
            // ============================================================

            new(
                "help.geometry.radius.formula",
                "Radius formula",
                "R = dimB / 2"),

            new(
                "help.geometry.radius.calculation",
                "Radius calculation",
                $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new(
                "help.geometry.axial-length.formula",
                "Total axial length formula",
                "Ltotal = dimA + 2 × dimC"),

            new(
                "help.geometry.axial-length.calculation",
                "Total axial length calculation",
                $"{F(d.DimAMm)} + 2 × {F(d.DimCMm)} = {F(d.TotalAxialLengthMm)} mm"),

            new(
                "help.geometry.liquid-height",
                "Physical liquid height",
                $"h = {F(d.LiquidHeightMm)} mm = {F(d.LiquidHeightM)} m"),

            // ============================================================
            // Horizontal cylindrical part
            // ============================================================

            new(
                "help.volume.segment-angle.formula",
                "Circular segment angle",
                "θ = 2 × acos((R - h) / R)"),

            new(
                "help.volume.segment-angle.calculation",
                "Circular segment angle calculation",
                $"θ = {F(d.SegmentAngleRad)} rad"),

            new(
                "help.volume.segment.formula",
                "Circular segment area formula",
                "Asegment = R² / 2 × (θ - sin θ)"),

            new(
                "help.volume.segment.calculation",
                "Current circular segment area",
                $"{F(d.RadiusM)}² / 2 × ({F(d.SegmentAngleRad)} - sin({F(d.SegmentAngleRad)})) = {F(d.SegmentAreaM2)} m²"),

            new(
                "help.volume.cylinder.formula",
                "Current cylindrical volume formula",
                "Vcyl = Asegment × dimA"),

            new(
                "help.volume.cylinder.calculation",
                "Current cylindrical volume",
                $"{F(d.SegmentAreaM2)} × {F(d.CylinderLengthM)} = {F(d.CylinderVolumeM3)} m³"),

            // ============================================================
            // Elliptical ends
            // ============================================================

            new(
                "help.volume.head-coordinate.formula",
                "Elliptical head vertical coordinate",
                "y = h - R"),

            new(
                "help.volume.head-coordinate.calculation",
                "Elliptical head vertical coordinate",
                $"{F(d.LiquidHeightM)} - {F(d.RadiusM)} = {F(d.HeadVerticalCoordinateM)} m"),

            new(
                "help.volume.head.formula",
                "One elliptical end formula",
                "Vhead = π × C × R / 2 × (y - y³ / (3 × R²) + 2 × R / 3)"),

            new(
                "help.volume.head.calculation",
                "Current volume of one elliptical end",
                $"π × {F(d.HeadDepthM)} × {F(d.RadiusM)} / 2 × " +
                $"({F(d.HeadVerticalCoordinateM)} - {F(d.HeadVerticalCoordinateM)}³ / " +
                $"(3 × {F(d.RadiusM)}²) + 2 × {F(d.RadiusM)} / 3) = {F(d.OneHeadVolumeM3)} m³"),

            new(
                "help.volume.heads.formula",
                "Two elliptical ends formula",
                "Vheads = 2 × Vhead"),

            new(
                "help.volume.heads.calculation",
                "Current volume of two elliptical ends",
                $"2 × {F(d.OneHeadVolumeM3)} = {F(d.TwoHeadsVolumeM3)} m³"),

            // ============================================================
            // Full geometry
            // ============================================================

            new(
                "help.volume.full-cylinder.calculation",
                "Full cylindrical volume",
                $"π × {F(d.RadiusM)}² × {F(d.CylinderLengthM)} = {F(d.FullCylinderVolumeM3)} m³"),

            new(
                "help.volume.full-head.calculation",
                "Full volume of one elliptical end",
                $"2 / 3 × π × {F(d.HeadDepthM)} × {F(d.RadiusM)}² = {F(d.FullOneHeadVolumeM3)} m³"),

            new(
                "help.volume.full-tank.formula",
                "Full Tank formula",
                "Vfull = VcylFull + 2 × VheadFull"),

            new(
                "help.volume.full-tank.calculation",
                "Full Tank volume",
                $"{F(d.FullCylinderVolumeM3)} + 2 × {F(d.FullOneHeadVolumeM3)} = {F(d.FullTankVolumeM3)} m³"),

            // ============================================================
            // Current result
            // ============================================================

            new(
                "help.result.volume.formula",
                "Current Volume formula",
                "Volume = Vcyl + Vheads"),

            new(
                "help.result.volume.calculation",
                "Current calculated Volume",
                $"{F(d.CylinderVolumeM3)} + {F(d.TwoHeadsVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    /// <summary>
    /// Единственный внутренний расчёт Type 2.
    ///
    /// И рабочий Volume, и Help получают данные отсюда. Поэтому справка не дублирует отдельную реализацию формул.
    /// </summary>
    private static Type2VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");

        if (dimA < 0 || dimB <= 0 || dimC < 0)
            return Type2VolumeDetails.Invalid(dimA, dimB, dimC);

        var cylinderLengthM = dimA * 0.001;
        var diameterM = dimB * 0.001;
        var radiusM = diameterM / 2.0;
        var radiusMm = dimB / 2.0;
        var headDepthM = dimC * 0.001;
        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, diameterM);

        // ============================================================
        // Horizontal cylinder.
        //
        // Площадь заполненного кругового сегмента рассчитывается
        // по точной формуле, затем умножается на длину цилиндра.
        // ============================================================
        var segmentAngleRad = CalculateSegmentAngle(radiusM, liquidHeightM);
        var segmentAreaM2 = CalculateCircularSegmentArea(radiusM, liquidHeightM);
        var cylinderVolumeM3 = segmentAreaM2 * cylinderLengthM;

        // ============================================================
        // Elliptical ends.
        //
        // Каждый торец является половиной эллипсоида вращения:
        //
        // axial semi-axis = dimC
        // radial semi-axis = R
        //
        // Для горизонтального уровня интегрируем площадь
        // горизонтальных сечений полуэллипсоида.
        // ============================================================
        var headVerticalCoordinateM = Math.Clamp(liquidHeightM - radiusM, -radiusM, radiusM);
        var oneHeadVolumeM3 = CalculateOneEllipticalHeadVolume(radiusM, headDepthM, liquidHeightM);
        var twoHeadsVolumeM3 = oneHeadVolumeM3 * 2.0;

        var fullCylinderVolumeM3 = Math.PI * radiusM * radiusM * cylinderLengthM;
        var fullOneHeadVolumeM3 = 2.0 * Math.PI * headDepthM * radiusM * radiusM / 3.0;
        var fullTankVolumeM3 = fullCylinderVolumeM3 + fullOneHeadVolumeM3 * 2.0;
        var volumeM3 = cylinderVolumeM3 + twoHeadsVolumeM3;

        return new Type2VolumeDetails(
            DimAMm: dimA,
            DimBMm: dimB,
            DimCMm: dimC,
            RadiusMm: radiusMm,
            RadiusM: radiusM,
            CylinderLengthM: cylinderLengthM,
            HeadDepthM: headDepthM,
            TotalAxialLengthMm: dimA + dimC * 2.0,
            LiquidHeightMm: liquidHeightM * 1000.0,
            LiquidHeightM: liquidHeightM,
            SegmentAngleRad: segmentAngleRad,
            SegmentAreaM2: segmentAreaM2,
            HeadVerticalCoordinateM: headVerticalCoordinateM,
            CylinderVolumeM3: cylinderVolumeM3,
            OneHeadVolumeM3: oneHeadVolumeM3,
            TwoHeadsVolumeM3: twoHeadsVolumeM3,
            FullCylinderVolumeM3: fullCylinderVolumeM3,
            FullOneHeadVolumeM3: fullOneHeadVolumeM3,
            FullTankVolumeM3: fullTankVolumeM3,
            VolumeM3: volumeM3);
    }

    // Центральный угол заполненного сегмента круга, rad.
    private static double CalculateSegmentAngle(double radiusM, double fillHeightM)
    {
        if (radiusM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= radiusM * 2.0)
            return Math.PI * 2.0;

        var argument = Math.Clamp((radiusM - fillHeightM) / radiusM, -1.0, 1.0);
        return 2.0 * Math.Acos(argument);
    }

    // Точная площадь горизонтального кругового сегмента.
    private static double CalculateCircularSegmentArea(double radiusM, double fillHeightM)
    {
        if (radiusM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= radiusM * 2.0)
            return Math.PI * radiusM * radiusM;

        var theta = CalculateSegmentAngle(radiusM, fillHeightM);
        return radiusM * radiusM * (theta - Math.Sin(theta)) / 2.0;
    }

    /// <summary>
    /// Точный частичный объём одного полуэллипсоидного торца.
    ///
    /// y = h - R
    ///
    /// V = π × C × R / 2 ×
    ///     (y - y³ / (3R²) + 2R/3)
    ///
    /// При h=0       -> 0.
    /// При h=R       -> половина полного объёма торца.
    /// При h=2R      -> полный объём 2/3 π C R².
    /// </summary>
    private static double CalculateOneEllipticalHeadVolume(double radiusM, double headDepthM, double fillHeightM)
    {
        if (radiusM <= 0 || headDepthM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= radiusM * 2.0)
            return 2.0 * Math.PI * headDepthM * radiusM * radiusM / 3.0;

        var y = Math.Clamp(fillHeightM - radiusM, -radiusM, radiusM);

        return Math.PI * headDepthM * radiusM / 2.0 * (y - y * y * y / (3.0 * radiusM * radiusM) + 2.0 * radiusM / 3.0);
    }

    private sealed record Type2VolumeDetails(
        double DimAMm,
        double DimBMm,
        double DimCMm,
        double RadiusMm,
        double RadiusM,
        double CylinderLengthM,
        double HeadDepthM,
        double TotalAxialLengthMm,
        double LiquidHeightMm,
        double LiquidHeightM,
        double SegmentAngleRad,
        double SegmentAreaM2,
        double HeadVerticalCoordinateM,
        double CylinderVolumeM3,
        double OneHeadVolumeM3,
        double TwoHeadsVolumeM3,
        double FullCylinderVolumeM3,
        double FullOneHeadVolumeM3,
        double FullTankVolumeM3,
        double VolumeM3)
    {
        public static Type2VolumeDetails Invalid(double dimA, double dimB, double dimC)
        {
            return new Type2VolumeDetails(
                dimA, dimB, dimC,
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN,
                double.NaN);
        }
    }
}