using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 5
//
// Вертикальная колонна со встроенным трубным пучком / ребойлером.
//
// dimA - высота цилиндрической части, mm.
// dimB - внутренний диаметр аппарата, mm.
// dimC - высота нижнего эллиптического днища, mm.
// dimD - высота трубного пучка, mm.
// dimE - расстояние от верха цилиндрической части
//        до верхней границы трубного пучка, mm.
// dimF - полный объём, занятый трубками пучка, L.
//
// Полная высота аппарата:
//
// Htotal = dimA + dimC
//
// Нижнее днище рассчитывается как точный полуэллипсоид.
//
// В цилиндрической части геометрический объём:
//
// Vbody = π × R² × H
//
// В зоне трубного пучка из геометрического объёма
// вычитается объём уже погружённой части труб:
//
// Vdisplaced = Vtubes × HbundleFilled / dimD
//
// Старые:
// distanceA
// distanceB
// distToDistanceA
// probeLength
// коэффициент 0.85
//
// в Type 5 больше не используются.
// ============================================================
public sealed class TankType5VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10, minimum: 1d),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12),
        Dimension("dimD", "dimD", 13, minimum: 1d),
        Dimension("dimE", "dimE", 14),
        Dimension("dimF", "dimF — reboiler tube volume", 15, unit: "L", minimum: 0d, step: 0.1, decimals: 1));

    public override string Code => "tank.volume.type5";
    public override string Name => "Type 5 — column with internal reboiler";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Полная физическая высота:
    //
    // нижнее эллиптическое днище
    // +
    // цилиндрическая часть.
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimA") + parameters.GetRequiredDouble("dimC");
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        return CalculateDetails(parameters, liquidHeightMm).VolumeM3;
    }

    // ============================================================
    // HELP
    //
    // Help использует тот же CalculateDetails(), что и Runtime.
    // Никакой второй копии формул в WEB нет.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Общая геометрия.
            // ============================================================

            new("help.geometry.radius.formula", "Radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.total-height.formula", "Total height formula", "Htotal = dimC + dimA"),
            new("help.geometry.total-height.calculation", "Total height calculation", $"{F(d.DimCMm)} + {F(d.DimAMm)} = {F(d.TotalHeightMm)} mm"),

            new("help.geometry.area.formula", "Cylindrical cross-section formula", "S = π × R²"),
            new("help.geometry.area.calculation", "Cylindrical cross-section", $"π × {F(d.RadiusM)}² = {F(d.CircleAreaM2)} m²"),

            // ============================================================
            // Нижнее эллиптическое днище.
            // ============================================================

            new("help.volume.head.formula", "Lower elliptical head formula", "Vhead(h) = π × R² × (h² / C - h³ / (3 × C²))"),
            new("help.volume.head.calculation", "Current lower head volume", d.HeadHeightM > 0
                ? $"π × {F(d.RadiusM)}² × ({F(d.HeadFillM)}² / {F(d.HeadHeightM)} - {F(d.HeadFillM)}³ / (3 × {F(d.HeadHeightM)}²)) = {F(d.HeadVolumeM3)} m³"
                : "No lower elliptical head."),

            new("help.volume.full-head.formula", "Full lower head formula", "VheadFull = 2 / 3 × π × R² × C"),
            new("help.volume.full-head.calculation", "Full lower head volume", $"{F(d.FullHeadVolumeM3)} m³"),

            // ============================================================
            // Трубный пучок.
            // ============================================================

            new("help.geometry.bundle-bottom.formula", "Tube bundle bottom elevation", "HbundleBottom = dimC + dimA - dimE - dimD"),
            new("help.geometry.bundle-bottom.calculation", "Tube bundle bottom elevation", $"{F(d.DimCMm)} + {F(d.DimAMm)} - {F(d.DimEMm)} - {F(d.DimDMm)} = {F(d.BundleBottomMm)} mm"),

            new("help.geometry.bundle-top.formula", "Tube bundle top elevation", "HbundleTop = dimC + dimA - dimE"),
            new("help.geometry.bundle-top.calculation", "Tube bundle top elevation", $"{F(d.DimCMm)} + {F(d.DimAMm)} - {F(d.DimEMm)} = {F(d.BundleTopMm)} mm"),

            new("help.geometry.tube-volume.formula", "Tube displacement volume", "Vtubes = dimF / 1000"),
            new("help.geometry.tube-volume.calculation", "Tube displacement volume", $"{F(d.DimFL)} / 1000 = {F(d.TubeVolumeM3)} m³"),

            // ============================================================
            // Текущий уровень внутри цилиндрической части.
            // ============================================================

            new("help.volume.body-height.formula", "Current cylindrical fill height", "Hbody = clamp(Hliquid - dimC, 0, dimA)"),
            new("help.volume.body-height.calculation", "Current cylindrical fill height", $"Hbody = {F(d.BodyFillM)} m"),

            new("help.volume.body-gross.formula", "Gross cylindrical volume formula", "VbodyGross = S × Hbody"),
            new("help.volume.body-gross.calculation", "Gross cylindrical volume", $"{F(d.CircleAreaM2)} × {F(d.BodyFillM)} = {F(d.GrossBodyVolumeM3)} m³"),

            // ============================================================
            // Погружённая часть труб.
            //
            // Трубки считаются равномерно распределёнными
            // по всей высоте dimD.
            // ============================================================

            new("help.volume.bundle-fill.formula", "Flooded tube bundle height", "HbundleFilled = clamp(Hliquid - HbundleBottom, 0, dimD)"),
            new("help.volume.bundle-fill.calculation", "Flooded tube bundle height", $"HbundleFilled = {F(d.BundleFilledM)} m"),

            new("help.volume.tube-displacement.formula", "Flooded tube displacement formula", "Vdisplaced = Vtubes × HbundleFilled / dimD"),
            new("help.volume.tube-displacement.calculation", "Flooded tube displacement", $"{F(d.TubeVolumeM3)} × {F(d.BundleFilledM)} / {F(d.BundleHeightM)} = {F(d.FloodedTubeVolumeM3)} m³"),

            // ============================================================
            // Полный полезный объём.
            // ============================================================

            new("help.volume.full-gross.formula", "Gross Tank volume formula", "Vgross = VheadFull + S × dimA"),
            new("help.volume.full-gross.calculation", "Gross Tank volume", $"{F(d.FullHeadVolumeM3)} + {F(d.CircleAreaM2)} × {F(d.BodyHeightM)} = {F(d.FullGrossVolumeM3)} m³"),

            new("help.volume.full-net.formula", "Net Tank volume formula", "VnetFull = Vgross - Vtubes"),
            new("help.volume.full-net.calculation", "Net Tank volume", $"{F(d.FullGrossVolumeM3)} - {F(d.TubeVolumeM3)} = {F(d.FullNetVolumeM3)} m³"),

            // ============================================================
            // Текущий результат.
            // ============================================================

            new("help.volume.current-region", "Current liquid region", d.CurrentRegion),

            new("help.result.volume.formula", "Current Volume formula", "Volume = Vhead + VbodyGross - Vdisplaced"),
            new("help.result.volume.calculation", "Current calculated Volume", $"{F(d.HeadVolumeM3)} + {F(d.GrossBodyVolumeM3)} - {F(d.FloodedTubeVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Единственный расчёт геометрии Type 5.
    // ============================================================
    private static Type5VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");
        var dimE = parameters.GetRequiredDouble("dimE");
        var dimF = parameters.GetRequiredDouble("dimF");

        // Трубный пучок Type 5 должен полностью находиться
        // внутри цилиндрической части.
        //
        // dimE + dimD <= dimA.
        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || !double.IsFinite(dimD) || !double.IsFinite(dimE) || !double.IsFinite(dimF)
            || dimA <= 0 || dimB <= 0 || dimC < 0 || dimD <= 0 || dimE < 0 || dimF < 0 || dimD + dimE > dimA)
        {
            return Type5VolumeDetails.Invalid(dimA, dimB, dimC, dimD, dimE, dimF);
        }

        var radiusMm = dimB / 2.0;
        var radiusM = radiusMm * 0.001;
        var bodyHeightM = dimA * 0.001;
        var headHeightM = dimC * 0.001;
        var bundleHeightM = dimD * 0.001;
        var upperSectionHeightM = dimE * 0.001;
        var tubeVolumeM3 = dimF * 0.001;
        var totalHeightMm = dimA + dimC;
        var totalHeightM = totalHeightMm * 0.001;
        var circleAreaM2 = Math.PI * radiusM * radiusM;

        // Полный геометрический объём области, в которой расположен пучок.
        //
        // Объём труб физически не может превышать этот объём.
        var grossBundleRegionVolumeM3 = circleAreaM2 * bundleHeightM;

        if (tubeVolumeM3 > grossBundleRegionVolumeM3)
            return Type5VolumeDetails.Invalid(dimA, dimB, dimC, dimD, dimE, dimF);

        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);

        // ============================================================
        // Нижнее эллиптическое днище.
        // ============================================================

        var headFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);
        var headVolumeM3 = CalculateLowerHeadVolume(radiusM, headHeightM, headFillM);
        var fullHeadVolumeM3 = headHeightM > 0 ? 2.0 * Math.PI * radiusM * radiusM * headHeightM / 3.0 : 0.0;

        // ============================================================
        // Цилиндрическая часть.
        // ============================================================

        var bodyFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var grossBodyVolumeM3 = circleAreaM2 * bodyFillM;

        // ============================================================
        // Положение трубного пучка.
        //
        // dimE измеряется сверху цилиндрической части.
        //
        // В координатах от самого нижнего дна Tank:
        //
        // bundleBottom = dimC + dimA - dimE - dimD
        // bundleTop    = dimC + dimA - dimE
        // ============================================================

        var bundleBottomM = headHeightM + bodyHeightM - upperSectionHeightM - bundleHeightM;
        var bundleTopM = headHeightM + bodyHeightM - upperSectionHeightM;

        // ============================================================
        // Объём погружённых труб.
        //
        // Трубки проходят по всей высоте dimD,
        // поэтому их вытесняющий объём возрастает линейно
        // от 0 до полного dimF.
        // ============================================================

        var bundleFilledM = Math.Clamp(liquidHeightM - bundleBottomM, 0.0, bundleHeightM);
        var floodedTubeVolumeM3 = tubeVolumeM3 * bundleFilledM / bundleHeightM;

        // ============================================================
        // Итоговые объёмы.
        // ============================================================

        var fullGrossVolumeM3 = fullHeadVolumeM3 + circleAreaM2 * bodyHeightM;
        var fullNetVolumeM3 = fullGrossVolumeM3 - tubeVolumeM3;
        var volumeM3 = headVolumeM3 + grossBodyVolumeM3 - floodedTubeVolumeM3;

        var currentRegion = liquidHeightM <= headHeightM
            ? "Lower elliptical head"
            : liquidHeightM <= bundleBottomM
                ? "Cylindrical section below tube bundle"
                : liquidHeightM <= bundleTopM
                    ? "Tube bundle region"
                    : "Cylindrical section above tube bundle";

        return new Type5VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD, DimEMm: dimE, DimFL: dimF,
            RadiusMm: radiusMm, RadiusM: radiusM, BodyHeightM: bodyHeightM, HeadHeightM: headHeightM,
            BundleHeightM: bundleHeightM, UpperSectionHeightM: upperSectionHeightM,
            TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM, CircleAreaM2: circleAreaM2,
            BundleBottomMm: bundleBottomM * 1000.0, BundleTopMm: bundleTopM * 1000.0,
            TubeVolumeM3: tubeVolumeM3, GrossBundleRegionVolumeM3: grossBundleRegionVolumeM3,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            HeadFillM: headFillM, HeadVolumeM3: headVolumeM3, FullHeadVolumeM3: fullHeadVolumeM3,
            BodyFillM: bodyFillM, GrossBodyVolumeM3: grossBodyVolumeM3,
            BundleFilledM: bundleFilledM, FloodedTubeVolumeM3: floodedTubeVolumeM3,
            FullGrossVolumeM3: fullGrossVolumeM3, FullNetVolumeM3: fullNetVolumeM3,
            VolumeM3: volumeM3, CurrentRegion: currentRegion);
    }

    // ============================================================
    // Точный частичный объём нижнего полуэллипсоида.
    //
    // h измеряется от самой нижней точки днища вверх.
    //
    // При h = C:
    //
    // V = 2/3 × π × R² × C.
    // ============================================================
    private static double CalculateLowerHeadVolume(double radiusM, double headHeightM, double fillHeightM)
    {
        if (radiusM <= 0 || headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        var h = Math.Min(fillHeightM, headHeightM);
        return Math.PI * radiusM * radiusM * (h * h / headHeightM - h * h * h / (3.0 * headHeightM * headHeightM));
    }

    private sealed record Type5VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm, double DimDMm, double DimEMm, double DimFL,
        double RadiusMm, double RadiusM, double BodyHeightM, double HeadHeightM,
        double BundleHeightM, double UpperSectionHeightM,
        double TotalHeightMm, double TotalHeightM, double CircleAreaM2,
        double BundleBottomMm, double BundleTopMm,
        double TubeVolumeM3, double GrossBundleRegionVolumeM3,
        double LiquidHeightMm, double LiquidHeightM,
        double HeadFillM, double HeadVolumeM3, double FullHeadVolumeM3,
        double BodyFillM, double GrossBodyVolumeM3,
        double BundleFilledM, double FloodedTubeVolumeM3,
        double FullGrossVolumeM3, double FullNetVolumeM3,
        double VolumeM3, string CurrentRegion)
    {
        public static Type5VolumeDetails Invalid(double dimA, double dimB, double dimC, double dimD, double dimE, double dimF)
        {
            return new Type5VolumeDetails(
                dimA, dimB, dimC, dimD, dimE, dimF,
                0, 0, 0, 0,
                0, 0,
                0, 0, 0,
                0, 0,
                0, 0,
                0, 0,
                0, 0, 0,
                0, 0,
                0, 0,
                0, 0,
                double.NaN, "Invalid geometry");
        }
    }
}