using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 6
//
// Вертикальный Tank:
//
// верхнее коническое днище
// +
// цилиндрическая часть
// +
// нижнее коническое днище.
//
// dimA - высота цилиндрической части, mm.
// dimB - внутренний диаметр Tank, mm.
// dimC - высота каждого конического днища, mm.
//
// Полная высота:
//
// Htotal = dimA + 2 × dimC
//
// В отличие от старой реализации здесь больше нет:
//
// distanceA
// distanceB
// distToDistanceA
// probeLength
// коэффициента 0.8.
//
// Все объёмы считаются по точной геометрии конуса.
// ============================================================
public sealed class TankType6VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12));

    public override string Code => "tank.volume.type6";
    public override string Name => "Type 6 — vertical, two conical heads";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Полная физическая высота Tank:
    //
    // нижний конус + цилиндр + верхний конус.
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
    // что и настоящий Runtime-расчёт.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Geometry
            // ============================================================

            new("help.geometry.radius.formula", "Radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.total-height.formula", "Total height formula", "Htotal = dimA + 2 × dimC"),
            new("help.geometry.total-height.calculation", "Total height calculation", $"{F(d.DimAMm)} + 2 × {F(d.DimCMm)} = {F(d.TotalHeightMm)} mm"),

            new("help.geometry.area.formula", "Cylindrical cross-section formula", "S = π × R²"),
            new("help.geometry.area.calculation", "Cylindrical cross-section", $"π × {F(d.RadiusM)}² = {F(d.CircleAreaM2)} m²"),

            // ============================================================
            // Full conical head
            // ============================================================

            new("help.volume.head.formula", "Full conical head formula", "Vcone = 1 / 3 × π × R² × C"),
            new("help.volume.head.calculation", "One full conical head", $"1 / 3 × π × {F(d.RadiusM)}² × {F(d.HeadHeightM)} = {F(d.FullHeadVolumeM3)} m³"),

            // ============================================================
            // Cylinder
            // ============================================================

            new("help.volume.cylinder.formula", "Full cylinder formula", "Vcyl = π × R² × dimA"),
            new("help.volume.cylinder.calculation", "Full cylindrical part", $"π × {F(d.RadiusM)}² × {F(d.BodyHeightM)} = {F(d.FullCylinderVolumeM3)} m³"),

            // ============================================================
            // Full Tank
            // ============================================================

            new("help.volume.full-tank.formula", "Full Tank formula", "Vfull = VlowerCone + Vcyl + VupperCone"),
            new("help.volume.full-tank.calculation", "Full Tank volume", $"{F(d.FullHeadVolumeM3)} + {F(d.FullCylinderVolumeM3)} + {F(d.FullHeadVolumeM3)} = {F(d.FullTankVolumeM3)} m³"),

            // ============================================================
            // Current region
            // ============================================================

            new("help.volume.current.region", "Current liquid region", d.CurrentRegion),

            // ============================================================
            // Lower cone
            //
            // Нижний конус начинается вершиной снизу.
            //
            // Радиус жидкости на высоте h:
            //
            // r = R × h / C
            //
            // Поэтому:
            //
            // V = 1/3 × π × r² × h
            //   = π × R² × h³ / (3 × C²)
            // ============================================================

            new("help.volume.lower.formula", "Lower cone partial formula", "Vlower(h) = π × R² × h³ / (3 × C²)"),
            new("help.volume.lower.calculation", "Current lower cone volume", d.HeadHeightM > 0
                ? $"π × {F(d.RadiusM)}² × {F(d.LowerHeadFillM)}³ / (3 × {F(d.HeadHeightM)}²) = {F(d.LowerHeadVolumeM3)} m³"
                : "No lower conical head."),

            // ============================================================
            // Cylinder
            // ============================================================

            new("help.volume.current-cylinder.formula", "Current cylinder formula", "Vcyl(h) = π × R² × hcyl"),
            new("help.volume.current-cylinder.calculation", "Current cylindrical volume", $"π × {F(d.RadiusM)}² × {F(d.CylinderFillM)} = {F(d.CylinderVolumeM3)} m³"),

            // ============================================================
            // Upper cone
            //
            // Верхний конус начинается полным радиусом R
            // и сужается к верхней вершине.
            //
            // x измеряется от основания верхнего конуса вверх.
            //
            // Vupper(x) =
            //
            // πR² × (x - x²/C + x³/(3C²))
            // ============================================================

            new("help.volume.upper.formula", "Upper cone partial formula", "Vupper(x) = π × R² × (x - x² / C + x³ / (3 × C²))"),
            new("help.volume.upper.calculation", "Current upper cone volume", d.HeadHeightM > 0
                ? $"π × {F(d.RadiusM)}² × ({F(d.UpperHeadFillM)} - {F(d.UpperHeadFillM)}² / {F(d.HeadHeightM)} + {F(d.UpperHeadFillM)}³ / (3 × {F(d.HeadHeightM)}²)) = {F(d.UpperHeadVolumeM3)} m³"
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
    // И Runtime, и Help используют эти же значения.
    // ============================================================
    private static Type6VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");

        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || dimA < 0 || dimB <= 0 || dimC < 0)
            return Type6VolumeDetails.Invalid(dimA, dimB, dimC);

        var radiusMm = dimB / 2.0;
        var radiusM = radiusMm * 0.001;
        var bodyHeightM = dimA * 0.001;
        var headHeightM = dimC * 0.001;
        var totalHeightMm = dimA + dimC * 2.0;
        var totalHeightM = totalHeightMm * 0.001;
        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);
        var circleAreaM2 = Math.PI * radiusM * radiusM;

        // ============================================================
        // dimC = 0
        //
        // Оба конуса вырождаются в плоские крышки,
        // поэтому Tank становится обычным цилиндром.
        // ============================================================

        if (headHeightM <= 0)
        {
            // При dimC = 0 оба конических днища вырождаются
            // в плоские крышки, и Tank становится обычным цилиндром.
            //
            // Используем отдельные имена переменных для fallback,
            // чтобы они не конфликтовали с основным расчётом ниже.
            var flatCylinderFillM = Math.Clamp(liquidHeightM, 0.0, bodyHeightM);
            var flatCylinderVolumeM3 = circleAreaM2 * flatCylinderFillM;
            var flatFullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;

            return new Type6VolumeDetails(
                DimAMm: dimA, DimBMm: dimB, DimCMm: dimC,
                RadiusMm: radiusMm, RadiusM: radiusM,
                BodyHeightM: bodyHeightM, HeadHeightM: 0.0,
                TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM,
                LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
                CircleAreaM2: circleAreaM2,
                FullHeadVolumeM3: 0.0, FullCylinderVolumeM3: flatFullCylinderVolumeM3, FullTankVolumeM3: flatFullCylinderVolumeM3,
                LowerHeadFillM: 0.0, LowerHeadVolumeM3: 0.0,
                CylinderFillM: flatCylinderFillM, CylinderVolumeM3: flatCylinderVolumeM3,
                UpperHeadFillM: 0.0, UpperHeadVolumeM3: 0.0,
                VolumeM3: flatCylinderVolumeM3,
                CurrentRegion: "Cylindrical part");
        }

        // ============================================================
        // Full geometry
        // ============================================================

        var fullHeadVolumeM3 = circleAreaM2 * headHeightM / 3.0;
        var fullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;
        var fullTankVolumeM3 = fullHeadVolumeM3 + fullCylinderVolumeM3 + fullHeadVolumeM3;

        // ============================================================
        // Lower conical head
        // ============================================================

        var lowerHeadFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);
        var lowerHeadVolumeM3 = CalculateLowerConeVolume(radiusM, headHeightM, lowerHeadFillM);

        // ============================================================
        // Cylindrical part
        // ============================================================

        var cylinderFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var cylinderVolumeM3 = circleAreaM2 * cylinderFillM;

        // ============================================================
        // Upper conical head
        // ============================================================

        var upperHeadFillM = Math.Clamp(liquidHeightM - headHeightM - bodyHeightM, 0.0, headHeightM);
        var upperHeadVolumeM3 = CalculateUpperConeVolume(radiusM, headHeightM, upperHeadFillM);

        // ============================================================
        // Final
        // ============================================================

        var volumeM3 = lowerHeadVolumeM3 + cylinderVolumeM3 + upperHeadVolumeM3;
        var currentRegion = liquidHeightM <= headHeightM ? "Lower conical head" : liquidHeightM <= headHeightM + bodyHeightM ? "Cylindrical part" : "Upper conical head";

        return new Type6VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC,
            RadiusMm: radiusMm, RadiusM: radiusM,
            BodyHeightM: bodyHeightM, HeadHeightM: headHeightM,
            TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            CircleAreaM2: circleAreaM2,
            FullHeadVolumeM3: fullHeadVolumeM3, FullCylinderVolumeM3: fullCylinderVolumeM3, FullTankVolumeM3: fullTankVolumeM3,
            LowerHeadFillM: lowerHeadFillM, LowerHeadVolumeM3: lowerHeadVolumeM3,
            CylinderFillM: cylinderFillM, CylinderVolumeM3: cylinderVolumeM3,
            UpperHeadFillM: upperHeadFillM, UpperHeadVolumeM3: upperHeadVolumeM3,
            VolumeM3: volumeM3,
            CurrentRegion: currentRegion);
    }

    // Нижний конус:
    //
    // h отсчитывается от нижней вершины вверх.
    //
    // r(h) = R × h / C.
    //
    // V = 1/3 × π × r² × h.
    private static double CalculateLowerConeVolume(double radiusM, double headHeightM, double fillHeightM)
    {
        if (radiusM <= 0 || headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        var h = Math.Min(fillHeightM, headHeightM);
        return Math.PI * radiusM * radiusM * h * h * h / (3.0 * headHeightM * headHeightM);
    }

    // Верхний конус:
    //
    // x отсчитывается от его основания вверх.
    //
    // Радиус горизонтального сечения:
    //
    // r(x) = R × (1 - x / C).
    //
    // После интегрирования площади сечения:
    //
    // V = πR² × (x - x²/C + x³/(3C²)).
    private static double CalculateUpperConeVolume(double radiusM, double headHeightM, double fillHeightM)
    {
        if (radiusM <= 0 || headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        var x = Math.Min(fillHeightM, headHeightM);
        return Math.PI * radiusM * radiusM * (x - x * x / headHeightM + x * x * x / (3.0 * headHeightM * headHeightM));
    }

    private sealed record Type6VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm,
        double RadiusMm, double RadiusM,
        double BodyHeightM, double HeadHeightM,
        double TotalHeightMm, double TotalHeightM,
        double LiquidHeightMm, double LiquidHeightM,
        double CircleAreaM2,
        double FullHeadVolumeM3, double FullCylinderVolumeM3, double FullTankVolumeM3,
        double LowerHeadFillM, double LowerHeadVolumeM3,
        double CylinderFillM, double CylinderVolumeM3,
        double UpperHeadFillM, double UpperHeadVolumeM3,
        double VolumeM3,
        string CurrentRegion)
    {
        public static Type6VolumeDetails Invalid(double dimA, double dimB, double dimC)
        {
            return new Type6VolumeDetails(
                dimA, dimB, dimC,
                0, 0,
                0, 0,
                0, 0,
                0, 0,
                0,
                0, 0, 0,
                0, 0,
                0, 0,
                0, 0,
                double.NaN,
                "Invalid geometry");
        }
    }
}