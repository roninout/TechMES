using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 1.
///
/// Вертикальный Tank:
///
/// верхнее полуэллипсоидное днище
/// +
/// цилиндрическая часть
/// +
/// нижнее полуэллипсоидное днище.
///
/// dimA - высота цилиндрической части, mm.
/// dimB - внутренний диаметр, mm.
/// dimC - глубина каждого эллиптического днища, mm.
///
/// Все геометрические формулы Type 1,
/// промежуточные объёмы и Help находятся в этом классе.
///
/// WEB не знает формул Type 1.
/// </summary>
public sealed class TankType1VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition>
        ParameterDefinitions = CreateParameters(
                Dimension("dimA", "dimA", 10),
                Dimension("dimB", "dimB", 11),
                Dimension("dimC", "dimC", 12));

    public override string Code => "tank.volume.type1";

    public override string Name => "Type 1 — vertical, two elliptical heads";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    /// <summary>
    /// Полная физическая высота Type 1:
    ///
    /// верхнее днище
    /// + цилиндр
    /// + нижнее днище.
    /// </summary>
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimC = parameters.GetRequiredDouble("dimC");

        return dimA + dimC * 2.0;
    }


    /// <summary>
    /// Основной расчёт Volume.
    ///
    /// levelMm к этому моменту уже означает
    /// физическую высоту жидкости от самого дна Tank.
    /// </summary>
    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        var details = CalculateDetails(parameters, liquidHeightMm);
        return details.VolumeM3;
    }


    /// <summary>
    /// Формирует Help именно для Type 1.
    ///
    /// Здесь находятся:
    ///
    /// - формулы геометрии;
    /// - формулы эллиптических днищ;
    /// - подстановка текущих реальных значений;
    /// - промежуточные объёмы;
    /// - итоговый Volume.
    ///
    /// Поэтому WEB ничего из этой математики не дублирует.
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
                "help.geometry.total-height.formula",
                "Total height formula",
                "Htotal = dimA + 2 × dimC"),

            new(
                "help.geometry.total-height.calculation",
                "Total height calculation",
                $"{F(d.DimAMm)} + 2 × {F(d.DimCMm)} = {F(d.TotalHeightMm)} mm"),


            // ============================================================
            // Full elliptical head
            // ============================================================

            new(
                "help.volume.head.formula",
                "Full elliptical head formula",
                "Vhead = 2 / 3 × π × R² × C"),

            new(
                "help.volume.head.calculation",
                "One full elliptical head",
                $"2 / 3 × π × {F(d.RadiusM)}² × {F(d.HeadHeightM)} = {F(d.FullHeadVolumeM3)} m³"),


            // ============================================================
            // Full cylindrical part
            // ============================================================

            new(
                "help.volume.cylinder.formula",
                "Full cylinder formula",
                "Vcyl = π × R² × Hcyl"),

            new(
                "help.volume.cylinder.calculation",
                "Full cylindrical part",
                $"π × {F(d.RadiusM)}² × {F(d.BodyHeightM)} = {F(d.FullCylinderVolumeM3)} m³"),


            // ============================================================
            // Full Tank
            // ============================================================

            new(
                "help.volume.full-tank.formula",
                "Full Tank formula",
                "Vfull = VlowerHead + Vcyl + VupperHead"),

            new(
                "help.volume.full-tank.calculation",
                "Full Tank volume",
                $"{F(d.FullHeadVolumeM3)} + {F(d.FullCylinderVolumeM3)} + {F(d.FullHeadVolumeM3)} = {F(d.FullTankVolumeM3)} m³"),


            // ============================================================
            // Current liquid position
            // ============================================================

            new(
                "help.volume.current.region",
                "Current liquid region",
                d.CurrentRegion),


            new(
                "help.volume.lower.formula",
                "Lower head partial formula",
                "Vlower(h) = π × R² × (h² / C - h³ / (3 × C²))"),

            new(
                "help.volume.lower.calculation",
                "Current lower head volume",
                BuildLowerHeadCalculation(d)),


            new(
                "help.volume.current-cylinder.formula",
                "Current cylinder formula",
                "Vcyl(h) = π × R² × hcyl"),

            new(
                "help.volume.current-cylinder.calculation",
                "Current cylindrical volume",
                $"π × {F(d.RadiusM)}² × {F(d.CylinderFillM)} = {F(d.CylinderVolumeM3)} m³"),


            new(
                "help.volume.upper.formula",
                "Upper head partial formula",
                "Vupper(x) = π × R² × (x - x³ / (3 × C²))"),

            new(
                "help.volume.upper.calculation",
                "Current upper head volume",
                BuildUpperHeadCalculation(d)),


            // ============================================================
            // Current result
            // ============================================================

            new(
                "help.result.volume.formula",
                "Current Volume formula",
                "Volume = Vlower + Vcyl + Vupper"),

            new(
                "help.result.volume.calculation",
                "Current calculated Volume",
                $"{F(d.LowerHeadVolumeM3)} + {F(d.CylinderVolumeM3)} + {F(d.UpperHeadVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }


    /// <summary>
    /// Выполняет единственный внутренний расчёт геометрии Type 1.
    ///
    /// И основной Volume, и Help используют этот же метод.
    ///
    /// Благодаря этому формулы Help не живут отдельной жизнью
    /// от настоящего расчёта.
    /// </summary>
    private static Type1VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");

        if (dimA < 0 || dimB <= 0 || dimC < 0)
            return Type1VolumeDetails.Invalid(dimA, dimB, dimC);

        // ============================================================
        // Convert geometry from mm to m.
        // ============================================================

        var radiusMm = dimB / 2.0;
        var radiusM = radiusMm * 0.001;
        var bodyHeightM = dimA * 0.001;
        var headHeightM = dimC * 0.001;
        var totalHeightM = bodyHeightM + headHeightM * 2.0;
        var totalHeightMm = dimA + dimC * 2.0;
        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);
        var circleAreaM2 = Math.PI * radiusM * radiusM;


        // ============================================================
        // Flat-head fallback.
        //
        // Если dimC = 0, эллиптических днищ фактически нет.
        // Tank становится обычным вертикальным цилиндром.
        //
        // Для fallback используем отдельные имена локальных переменных,
        // чтобы они не конфликтовали с переменными основного расчёта ниже.
        // ============================================================

        if (headHeightM <= 0)
        {
            var flatCylinderFillM = Math.Clamp(liquidHeightM, 0.0, bodyHeightM);
            var flatCylinderVolumeM3 = circleAreaM2 * flatCylinderFillM;
            var flatFullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;

            return new Type1VolumeDetails(
                DimAMm: dimA,
                DimBMm: dimB,
                DimCMm: dimC,

                RadiusMm: radiusMm,
                RadiusM: radiusM,

                BodyHeightM: bodyHeightM,
                HeadHeightM: 0.0,

                TotalHeightMm: totalHeightMm,
                TotalHeightM: totalHeightM,

                LiquidHeightMm: liquidHeightM * 1000.0,
                LiquidHeightM: liquidHeightM,

                FullHeadVolumeM3: 0.0,
                FullCylinderVolumeM3: flatFullCylinderVolumeM3,
                FullTankVolumeM3: flatFullCylinderVolumeM3,

                LowerHeadFillM: 0.0,
                LowerHeadVolumeM3: 0.0,

                CylinderFillM: flatCylinderFillM,
                CylinderVolumeM3: flatCylinderVolumeM3,

                UpperHeadFillM: 0.0,
                UpperHeadVolumeM3: 0.0,

                VolumeM3: flatCylinderVolumeM3,

                CurrentRegion: "Cylindrical part");
        }


        // ============================================================
        // Full volumes.
        // ============================================================
        var fullHeadVolumeM3 = 2.0 * Math.PI * radiusM * radiusM * headHeightM / 3.0;
        var fullCylinderVolumeM3 = circleAreaM2 * bodyHeightM;
        var fullTankVolumeM3 = fullHeadVolumeM3 + fullCylinderVolumeM3 + fullHeadVolumeM3;

        // ============================================================
        // Lower elliptical head.
        // ============================================================
        var lowerHeadFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);
        var lowerHeadVolumeM3 = CalculateLowerHeadVolume(radiusM, headHeightM, lowerHeadFillM);

        // ============================================================
        // Cylindrical part.
        // ============================================================
        var cylinderFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var cylinderVolumeM3 = circleAreaM2 * cylinderFillM;

        // ============================================================
        // Upper elliptical head.
        // ============================================================
        var upperHeadFillM = Math.Clamp(liquidHeightM - headHeightM - bodyHeightM, 0.0, headHeightM);
        var upperHeadVolumeM3 = CalculateUpperHeadVolume(radiusM, headHeightM, upperHeadFillM);

        // ============================================================
        // Final current Volume.
        // ============================================================
        var volumeM3 = lowerHeadVolumeM3 + cylinderVolumeM3 + upperHeadVolumeM3;
        var currentRegion = liquidHeightM <= headHeightM ? "Lower elliptical head" : liquidHeightM <= headHeightM + bodyHeightM ? "Cylindrical part" : "Upper elliptical head";

        return new Type1VolumeDetails(
            DimAMm: dimA,
            DimBMm: dimB,
            DimCMm: dimC,

            RadiusMm: radiusMm,
            RadiusM: radiusM,

            BodyHeightM: bodyHeightM,
            HeadHeightM: headHeightM,

            TotalHeightMm: totalHeightMm,
            TotalHeightM: totalHeightM,

            LiquidHeightMm: liquidHeightM * 1000.0,
            LiquidHeightM: liquidHeightM,

            FullHeadVolumeM3: fullHeadVolumeM3,
            FullCylinderVolumeM3: fullCylinderVolumeM3,
            FullTankVolumeM3: fullTankVolumeM3,

            LowerHeadFillM: lowerHeadFillM,
            LowerHeadVolumeM3: lowerHeadVolumeM3,

            CylinderFillM: cylinderFillM,
            CylinderVolumeM3: cylinderVolumeM3,

            UpperHeadFillM: upperHeadFillM,
            UpperHeadVolumeM3: upperHeadVolumeM3,

            VolumeM3: volumeM3,

            CurrentRegion: currentRegion);
    }

    /// <summary>
    /// Частичный объём нижнего полуэллипсоида.
    ///
    /// h измеряется от самой нижней точки днища вверх.
    /// </summary>
    private static double CalculateLowerHeadVolume(double radiusM, double headHeightM, double fillHeightM)
    {
        if (headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        var h = Math.Min(fillHeightM, headHeightM);

        return Math.PI * radiusM * radiusM * ( h * h / headHeightM - h * h * h / (3.0 * headHeightM * headHeightM));
    }

    /// <summary>
    /// Частичный объём верхнего полуэллипсоида.
    ///
    /// x измеряется от основания верхнего днища вверх.
    /// </summary>
    private static double CalculateUpperHeadVolume(double radiusM, double headHeightM, double fillHeightM)
    {
        if (headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        var x = Math.Min(fillHeightM, headHeightM);

        return Math.PI * radiusM * radiusM * (x - x * x * x / (3.0 * headHeightM * headHeightM));
    }

    private static string BuildLowerHeadCalculation(Type1VolumeDetails d)
    {
        if (d.HeadHeightM <= 0)
            return "No elliptical lower head.";

        return
            $"π × {F(d.RadiusM)}² × " +
            $"({F(d.LowerHeadFillM)}² / {F(d.HeadHeightM)} - " +
            $"{F(d.LowerHeadFillM)}³ / (3 × {F(d.HeadHeightM)}²)) " +
            $"= {F(d.LowerHeadVolumeM3)} m³";
    }

    private static string BuildUpperHeadCalculation(Type1VolumeDetails d)
    {
        if (d.HeadHeightM <= 0)
            return "No elliptical upper head.";

        return
            $"π × {F(d.RadiusM)}² × " +
            $"({F(d.UpperHeadFillM)} - " +
            $"{F(d.UpperHeadFillM)}³ / (3 × {F(d.HeadHeightM)}²)) " +
            $"= {F(d.UpperHeadVolumeM3)} m³";
    }


    /// <summary>
    /// Все промежуточные значения одного расчёта Type 1.
    ///
    /// Это внутренний объект алгоритма.
    /// В Contracts и WEB его передавать не нужно.
    /// </summary>
    private sealed record Type1VolumeDetails(
        double DimAMm,
        double DimBMm,
        double DimCMm,

        double RadiusMm,
        double RadiusM,

        double BodyHeightM,
        double HeadHeightM,

        double TotalHeightMm,
        double TotalHeightM,

        double LiquidHeightMm,
        double LiquidHeightM,

        double FullHeadVolumeM3,
        double FullCylinderVolumeM3,
        double FullTankVolumeM3,

        double LowerHeadFillM,
        double LowerHeadVolumeM3,

        double CylinderFillM,
        double CylinderVolumeM3,

        double UpperHeadFillM,
        double UpperHeadVolumeM3,

        double VolumeM3,

        string CurrentRegion)
    {
        public static Type1VolumeDetails Invalid(double dimA, double dimB, double dimC)
        {
            return new Type1VolumeDetails(
                dimA,
                dimB,
                dimC,

                0,
                0,

                0,
                0,

                0,
                0,

                0,
                0,

                0,
                0,
                0,

                0,
                double.NaN,

                0,
                0,

                0,
                0,

                double.NaN,

                "Invalid geometry");
        }
    }
}