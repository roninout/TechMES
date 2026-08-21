using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 6
// Вертикальный Tank:
//
// верхнее коническое / усечённо-коническое днище
// +
// цилиндрическая часть
// +
// нижнее коническое / усечённо-коническое днище.
//
// dimA - высота цилиндрической части, mm.
// dimB - внутренний диаметр Tank, mm.
// dimC - высота каждого конического днища, mm.
// dimD - диаметр малого основания каждого днища, mm.
//
// Полная физическая высота:
// Htotal = dimA + 2 × dimC
//
// При: dimD = 0 получаем старый Type 6 с двумя настоящими конусами.
// При: dimD = dimB оба днища превращаются в цилиндрические продолжения.

// ============================================================
public sealed class TankType6VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12),
        Dimension("dimD", "dimD", 13, defaultValue: 0d, minimum: 0d));

    public override string Code => "tank.volume.type6";
    public override string Name => "Type 6 — vertical, two conical/frustum heads";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Полная физическая высота Tank:
    //
    // нижнее днище + цилиндр + верхнее днище.
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimA") + parameters.GetRequiredDouble("dimC") * 2.0;
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        return CalculateDetails(parameters, liquidHeightMm).VolumeM3;
    }

    // ============================================================
    // HELP
    //
    // Help использует тот же CalculateDetails(),
    // который используется реальным Runtime-расчётом.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Geometry
            // ============================================================

            new("help.geometry.radius.formula", "Main radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Main radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.small-radius.formula", "Small radius formula", "r = dimD / 2"),
            new("help.geometry.small-radius.calculation", "Small radius calculation", $"r = {F(d.DimDMm)} / 2 = {F(d.SmallRadiusMm)} mm = {F(d.SmallRadiusM)} m"),

            new("help.geometry.total-height.formula", "Total height formula", "Htotal = dimA + 2 × dimC"),
            new("help.geometry.total-height.calculation", "Total height calculation", $"{F(d.DimAMm)} + 2 × {F(d.DimCMm)} = {F(d.TotalHeightMm)} mm"),

            new("help.geometry.area.formula", "Cylindrical cross-section formula", "S = π × R²"),
            new("help.geometry.area.calculation", "Cylindrical cross-section", $"π × {F(d.RadiusM)}² = {F(d.CircleAreaM2)} m²"),

            // ============================================================
            // Full frustum head
            //
            // R - большой радиус.
            // r - малый радиус.
            // C - высота днища.
            // ============================================================

            new("help.volume.head.formula", "Full conical head formula", "Vhead = π × C / 3 × (R² + R × r + r²)"),
            new("help.volume.head.calculation", "One full conical head", $"π × {F(d.HeadHeightM)} / 3 × ({F(d.RadiusM)}² + {F(d.RadiusM)} × {F(d.SmallRadiusM)} + {F(d.SmallRadiusM)}²) = {F(d.FullHeadVolumeM3)} m³"),

            // ============================================================
            // Cylinder
            // ============================================================

            new("help.volume.cylinder.formula", "Full cylinder formula", "Vcyl = π × R² × dimA"),
            new("help.volume.cylinder.calculation", "Full cylindrical part", $"π × {F(d.RadiusM)}² × {F(d.BodyHeightM)} = {F(d.FullCylinderVolumeM3)} m³"),

            // ============================================================
            // Full Tank
            // ============================================================

            new("help.volume.full-tank.formula", "Full Tank formula", "Vfull = VlowerHead + Vcyl + VupperHead"),
            new("help.volume.full-tank.calculation", "Full Tank volume", $"{F(d.FullHeadVolumeM3)} + {F(d.FullCylinderVolumeM3)} + {F(d.FullHeadVolumeM3)} = {F(d.FullTankVolumeM3)} m³"),

            new("help.volume.current.region", "Current liquid region", d.CurrentRegion),

            // ============================================================
            // Lower frustum
            //
            // Нижнее днище начинается малым радиусом r.
            //
            // На высоте h:
            //
            // rh = r + (R - r) × h / C
            //
            // Заполненная часть сама является усечённым конусом:
            //
            // V = πh/3 × (r² + r×rh + rh²)
            // ============================================================

            new("help.volume.lower-radius.formula", "Lower head current radius", "rh = r + (R - r) × h / C"),
            new("help.volume.lower-radius.calculation", "Lower head current radius", $"rh = {F(d.LowerCurrentRadiusM)} m"),

            new("help.volume.lower.formula", "Lower head partial formula", "Vlower(h) = π × h / 3 × (r² + r × rh + rh²)"),
            new("help.volume.lower.calculation", "Current lower head volume", d.HeadHeightM > 0
                ? $"π × {F(d.LowerHeadFillM)} / 3 × ({F(d.SmallRadiusM)}² + {F(d.SmallRadiusM)} × {F(d.LowerCurrentRadiusM)} + {F(d.LowerCurrentRadiusM)}²) = {F(d.LowerHeadVolumeM3)} m³"
                : "No lower conical head."),

            // ============================================================
            // Cylinder
            // ============================================================

            new("help.volume.current-cylinder.formula", "Current cylinder formula", "Vcyl(h) = π × R² × hcyl"),
            new("help.volume.current-cylinder.calculation", "Current cylindrical volume", $"π × {F(d.RadiusM)}² × {F(d.CylinderFillM)} = {F(d.CylinderVolumeM3)} m³"),

            // ============================================================
            // Upper frustum
            //
            // Верхнее днище начинается большим радиусом R.
            //
            // На высоте x:
            //
            // rx = R - (R - r) × x / C
            //
            // Заполненная часть:
            //
            // V = πx/3 × (R² + R×rx + rx²)
            // ============================================================

            new("help.volume.upper-radius.formula", "Upper head current radius", "rx = R - (R - r) × x / C"),
            new("help.volume.upper-radius.calculation", "Upper head current radius", $"rx = {F(d.UpperCurrentRadiusM)} m"),

            new("help.volume.upper.formula", "Upper head partial formula", "Vupper(x) = π × x / 3 × (R² + R × rx + rx²)"),
            new("help.volume.upper.calculation", "Current upper head volume", d.HeadHeightM > 0
                ? $"π × {F(d.UpperHeadFillM)} / 3 × ({F(d.RadiusM)}² + {F(d.RadiusM)} × {F(d.UpperCurrentRadiusM)} + {F(d.UpperCurrentRadiusM)}²) = {F(d.UpperHeadVolumeM3)} m³"
                : "No upper conical head."),

            // ============================================================
            // Result
            // ============================================================

            new("help.result.volume.formula", "Current Volume formula", "Volume = Vlower + Vcyl + Vupper"),
            new("help.result.volume.calculation", "Current calculated Volume", $"{F(d.LowerHeadVolumeM3)} + {F(d.CylinderVolumeM3)} + {F(d.UpperHeadVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Единственный внутренний расчёт Type 6.
    //
    // Runtime и Help используют одну и ту же геометрию.
    // ============================================================
    private static Type6VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");

        // Малый диаметр днища не может превышать
        // диаметр основной цилиндрической части.
        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || !double.IsFinite(dimD)
            || dimA < 0 || dimB <= 0 || dimC < 0 || dimD < 0 || dimD > dimB)
        {
            return Type6VolumeDetails.Invalid(dimA, dimB, dimC, dimD);
        }

        var radiusMm = dimB / 2.0;
        var radiusM = radiusMm * 0.001;

        var smallRadiusMm = dimD / 2.0;
        var smallRadiusM = smallRadiusMm * 0.001;

        var bodyHeightM = dimA * 0.001;
        var headHeightM = dimC * 0.001;

        var totalHeightMm = dimA + dimC * 2.0;
        var totalHeightM = totalHeightMm * 0.001;
        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);

        var circleAreaM2 = Math.PI * radiusM * radiusM;

        // ============================================================
        // dimC = 0
        //
        // Оба днища имеют нулевую высоту.
        // Tank становится обычным цилиндром.
        //
        // dimD в этом случае на Volume не влияет.
        // ============================================================

        if (headHeightM <= 0)
        {
            var flatCylinderFillM = Math.Clamp(liquidHeightM, 0.0, bodyHeightM);
            var flatCylinderVolumeM3 = circleAreaM2 * flatCylinderFillM;
            var flatFullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;

            return new Type6VolumeDetails(
                DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD,
                RadiusMm: radiusMm, RadiusM: radiusM,
                SmallRadiusMm: smallRadiusMm, SmallRadiusM: smallRadiusM,
                BodyHeightM: bodyHeightM, HeadHeightM: 0.0,
                TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM,
                LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
                CircleAreaM2: circleAreaM2,
                FullHeadVolumeM3: 0.0, FullCylinderVolumeM3: flatFullCylinderVolumeM3, FullTankVolumeM3: flatFullCylinderVolumeM3,
                LowerHeadFillM: 0.0, LowerCurrentRadiusM: smallRadiusM, LowerHeadVolumeM3: 0.0,
                CylinderFillM: flatCylinderFillM, CylinderVolumeM3: flatCylinderVolumeM3,
                UpperHeadFillM: 0.0, UpperCurrentRadiusM: radiusM, UpperHeadVolumeM3: 0.0,
                VolumeM3: flatCylinderVolumeM3,
                CurrentRegion: "Cylindrical part");
        }

        // ============================================================
        // Full geometry
        //
        // Точный объём усечённого конуса:
        //
        // V = πC/3 × (R² + Rr + r²)
        // ============================================================

        var fullHeadVolumeM3 = CalculateFrustumVolume(radiusM, smallRadiusM, headHeightM);
        var fullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;
        var fullTankVolumeM3 = fullHeadVolumeM3 * 2.0 + fullCylinderVolumeM3;

        // ============================================================
        // Lower conical / frustum head
        //
        // h идёт от нижнего малого основания вверх.
        // ============================================================

        var lowerHeadFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);
        var lowerCurrentRadiusM = smallRadiusM + (radiusM - smallRadiusM) * lowerHeadFillM / headHeightM;
        var lowerHeadVolumeM3 = CalculateFrustumVolume(smallRadiusM, lowerCurrentRadiusM, lowerHeadFillM);

        // ============================================================
        // Cylindrical part
        // ============================================================

        var cylinderFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var cylinderVolumeM3 = circleAreaM2 * cylinderFillM;

        // ============================================================
        // Upper conical / frustum head
        //
        // x идёт от большого основания вверх.
        // ============================================================

        var upperHeadFillM = Math.Clamp(liquidHeightM - headHeightM - bodyHeightM, 0.0, headHeightM);
        var upperCurrentRadiusM = radiusM - (radiusM - smallRadiusM) * upperHeadFillM / headHeightM;
        var upperHeadVolumeM3 = CalculateFrustumVolume(radiusM, upperCurrentRadiusM, upperHeadFillM);

        // ============================================================
        // Final
        // ============================================================

        var volumeM3 = lowerHeadVolumeM3 + cylinderVolumeM3 + upperHeadVolumeM3;

        var currentRegion = liquidHeightM <= headHeightM
            ? "Lower conical/frustum head"
            : liquidHeightM <= headHeightM + bodyHeightM
                ? "Cylindrical part"
                : "Upper conical/frustum head";

        return new Type6VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD,
            RadiusMm: radiusMm, RadiusM: radiusM,
            SmallRadiusMm: smallRadiusMm, SmallRadiusM: smallRadiusM,
            BodyHeightM: bodyHeightM, HeadHeightM: headHeightM,
            TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            CircleAreaM2: circleAreaM2,
            FullHeadVolumeM3: fullHeadVolumeM3, FullCylinderVolumeM3: fullCylinderVolumeM3, FullTankVolumeM3: fullTankVolumeM3,
            LowerHeadFillM: lowerHeadFillM, LowerCurrentRadiusM: lowerCurrentRadiusM, LowerHeadVolumeM3: lowerHeadVolumeM3,
            CylinderFillM: cylinderFillM, CylinderVolumeM3: cylinderVolumeM3,
            UpperHeadFillM: upperHeadFillM, UpperCurrentRadiusM: upperCurrentRadiusM, UpperHeadVolumeM3: upperHeadVolumeM3,
            VolumeM3: volumeM3,
            CurrentRegion: currentRegion);
    }

    // ============================================================
    // Точный объём усечённого конуса.
    //
    // radius1M - радиус первого основания.
    // radius2M - радиус второго основания.
    // heightM  - расстояние между основаниями.
    //
    // Формула одинаково работает для:
    //
    // r = 0    -> обычный конус;
    // r = R    -> цилиндр;
    // 0<r<R    -> усечённый конус.
    // ============================================================
    private static double CalculateFrustumVolume(double radius1M, double radius2M, double heightM)
    {
        if (heightM <= 0)
            return 0.0;

        return Math.PI * heightM * (radius1M * radius1M + radius1M * radius2M + radius2M * radius2M) / 3.0;
    }

    private sealed record Type6VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm, double DimDMm,
        double RadiusMm, double RadiusM,
        double SmallRadiusMm, double SmallRadiusM,
        double BodyHeightM, double HeadHeightM,
        double TotalHeightMm, double TotalHeightM,
        double LiquidHeightMm, double LiquidHeightM,
        double CircleAreaM2,
        double FullHeadVolumeM3, double FullCylinderVolumeM3, double FullTankVolumeM3,
        double LowerHeadFillM, double LowerCurrentRadiusM, double LowerHeadVolumeM3,
        double CylinderFillM, double CylinderVolumeM3,
        double UpperHeadFillM, double UpperCurrentRadiusM, double UpperHeadVolumeM3,
        double VolumeM3,
        string CurrentRegion)
    {
        public static Type6VolumeDetails Invalid(double dimA, double dimB, double dimC, double dimD)
        {
            return new Type6VolumeDetails(
                dimA, dimB, dimC, dimD,
                0, 0,
                0, 0,
                0, 0,
                0, 0,
                0, 0,
                0,
                0, 0, 0,
                0, 0, double.NaN,
                0, 0,
                0, 0, double.NaN,
                double.NaN,
                "Invalid geometry");
        }
    }
}