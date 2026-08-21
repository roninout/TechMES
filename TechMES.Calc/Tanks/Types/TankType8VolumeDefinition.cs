using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 8
// Вертикальная колонна со встроенным трубным пучком / ребойлером и нижним коническим / усечённо-коническим днищем.
//
// Модель строится по Type 5.
//
// dimA - высота цилиндрической части, mm.
// dimB - внутренний диаметр аппарата, mm.
// dimC - высота нижнего конического днища, mm.
// dimD - высота трубного пучка, mm.
// dimE - расстояние от верха цилиндрической части до верхней границы трубного пучка, mm.
// dimF - полный объём, занятый трубками пучка, L.
// dimG - диаметр малого нижнего основания конического / усечённо-конического днища, mm.
//
// Полная физическая высота:
// Htotal = dimC + dimA
//
// Нижнее днище:
// dimG = 0 -> настоящий конус.
// 0 < dimG < dimB -> усечённый конус.
// dimG = dimB -> днище превращается в цилиндрическую секцию.
//
// Трубный пучок полностью находится только внутри цилиндрической части.
// В зоне трубного пучка из геометрического объёма вычитается уже погружённая часть труб:
// Vdisplaced = Vtubes × HbundleFilled / dimD
// ============================================================
public sealed class TankType8VolumeDefinition : TankTypeVolumeDefinitionBase
{
    // ============================================================
    // Type 8 использует те же начальные параметры ребойлера, что и Type 5.
    // dimG имеет default = 0: новый Type 8 по умолчанию имеет настоящее коническое днище.
    // ============================================================
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10, minimum: 1d),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12),
        Dimension("dimD", "dimD", 13, defaultValue: 2000d, minimum: 1d),
        Dimension("dimE", "dimE", 14, defaultValue: 200d),
        Dimension("dimF", "dimF — reboiler tube volume", 15, unit: "L", defaultValue: 100d, minimum: 0d, step: 0.1, decimals: 1),
        Dimension("dimG", "dimG — small bottom diameter", 16, defaultValue: 0d, minimum: 0d));

    public override string Code => "tank.volume.type8";
    public override string Name => "Type 8 — column with internal reboiler and conical bottom";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Полная физическая высота:
    //
    // нижнее коническое днище
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
    // Help использует тот же CalculateDetails(),
    // что и реальный Runtime-расчёт.
    //
    // Никакой второй копии формул в WEB не будет.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Общая геометрия.
            // ============================================================

            new("help.geometry.radius.formula", "Main radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Main radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.small-radius.formula", "Small bottom radius formula", "r = dimG / 2"),
            new("help.geometry.small-radius.calculation", "Small bottom radius calculation", $"r = {F(d.DimGMm)} / 2 = {F(d.SmallRadiusMm)} mm = {F(d.SmallRadiusM)} m"),

            new("help.geometry.total-height.formula", "Total height formula", "Htotal = dimC + dimA"),
            new("help.geometry.total-height.calculation", "Total height calculation", $"{F(d.DimCMm)} + {F(d.DimAMm)} = {F(d.TotalHeightMm)} mm"),

            new("help.geometry.area.formula", "Cylindrical cross-section formula", "S = π × R²"),
            new("help.geometry.area.calculation", "Cylindrical cross-section", $"π × {F(d.RadiusM)}² = {F(d.CircleAreaM2)} m²"),

            // ============================================================
            // Нижнее коническое / усечённо-коническое днище.
            //
            // На высоте h от нижнего малого основания:
            //
            // rh = r + (R - r) × h / C
            //
            // Заполненная часть сама является усечённым конусом.
            // ============================================================

            new("help.volume.head-radius.formula", "Current head radius formula", "rh = r + (R - r) × h / C"),
            new("help.volume.head-radius.calculation", "Current head radius", $"rh = {F(d.HeadCurrentRadiusM)} m"),

            new("help.volume.head.formula", "Lower conical head formula", "Vhead(h) = π × h / 3 × (r² + r × rh + rh²)"),
            new("help.volume.head.calculation", "Current lower head volume", d.HeadHeightM > 0
                ? $"π × {F(d.HeadFillM)} / 3 × ({F(d.SmallRadiusM)}² + {F(d.SmallRadiusM)} × {F(d.HeadCurrentRadiusM)} + {F(d.HeadCurrentRadiusM)}²) = {F(d.HeadVolumeM3)} m³"
                : "No lower conical head."),

            new("help.volume.full-head.formula", "Full lower head formula", "VheadFull = π × C / 3 × (r² + r × R + R²)"),
            new("help.volume.full-head.calculation", "Full lower head volume", $"π × {F(d.HeadHeightM)} / 3 × ({F(d.SmallRadiusM)}² + {F(d.SmallRadiusM)} × {F(d.RadiusM)} + {F(d.RadiusM)}²) = {F(d.FullHeadVolumeM3)} m³"),

            // ============================================================
            // Трубный пучок.
            //
            // Логика полностью соответствует Type 5.
            // ============================================================

            new("help.geometry.bundle-bottom.formula", "Tube bundle bottom elevation", "HbundleBottom = dimC + dimA - dimE - dimD"),
            new("help.geometry.bundle-bottom.calculation", "Tube bundle bottom elevation", $"{F(d.DimCMm)} + {F(d.DimAMm)} - {F(d.DimEMm)} - {F(d.DimDMm)} = {F(d.BundleBottomMm)} mm"),

            new("help.geometry.bundle-top.formula", "Tube bundle top elevation", "HbundleTop = dimC + dimA - dimE"),
            new("help.geometry.bundle-top.calculation", "Tube bundle top elevation", $"{F(d.DimCMm)} + {F(d.DimAMm)} - {F(d.DimEMm)} = {F(d.BundleTopMm)} mm"),

            new("help.geometry.tube-volume.formula", "Tube displacement volume", "Vtubes = dimF / 1000"),
            new("help.geometry.tube-volume.calculation", "Tube displacement volume", $"{F(d.DimFL)} / 1000 = {F(d.TubeVolumeM3)} m³"),

            // ============================================================
            // Текущий уровень в цилиндрической части.
            // ============================================================

            new("help.volume.body-height.formula", "Current cylindrical fill height", "Hbody = clamp(Hliquid - dimC, 0, dimA)"),
            new("help.volume.body-height.calculation", "Current cylindrical fill height", $"Hbody = {F(d.BodyFillM)} m"),

            new("help.volume.body-gross.formula", "Gross cylindrical volume formula", "VbodyGross = S × Hbody"),
            new("help.volume.body-gross.calculation", "Gross cylindrical volume", $"{F(d.CircleAreaM2)} × {F(d.BodyFillM)} = {F(d.GrossBodyVolumeM3)} m³"),

            // ============================================================
            // Погружённая часть труб.
            //
            // Объём труб равномерно распределён
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
            // Итог.
            // ============================================================

            new("help.volume.current-region", "Current liquid region", d.CurrentRegion),

            new("help.result.volume.formula", "Current Volume formula", "Volume = Vhead + VbodyGross - Vdisplaced"),
            new("help.result.volume.calculation", "Current calculated Volume", $"{F(d.HeadVolumeM3)} + {F(d.GrossBodyVolumeM3)} - {F(d.FloodedTubeVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Единственный расчёт геометрии Type 8.
    // ============================================================
    private static Type8VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");
        var dimE = parameters.GetRequiredDouble("dimE");
        var dimF = parameters.GetRequiredDouble("dimF");
        var dimG = parameters.GetRequiredDouble("dimG");

        // ============================================================
        // Проверка геометрии.
        // Трубный пучок должен полностью находиться внутри цилиндрической части:
        // dimD + dimE <= dimA.
        //
        // Малое основание днища:
        // 0 <= dimG <= dimB.
        // ============================================================

        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || !double.IsFinite(dimD) || !double.IsFinite(dimE) || !double.IsFinite(dimF) || !double.IsFinite(dimG)
            || dimA <= 0 || dimB <= 0 || dimC < 0 || dimD <= 0 || dimE < 0 || dimF < 0 || dimG < 0 || dimG > dimB || dimD + dimE > dimA)
        {
            return Type8VolumeDetails.Invalid(dimA, dimB, dimC, dimD, dimE, dimF, dimG);
        }

        var radiusMm = dimB / 2.0;
        var radiusM = radiusMm * 0.001;

        var smallRadiusMm = dimG / 2.0;
        var smallRadiusM = smallRadiusMm * 0.001;

        var bodyHeightM = dimA * 0.001;
        var headHeightM = dimC * 0.001;

        var bundleHeightM = dimD * 0.001;
        var upperSectionHeightM = dimE * 0.001;
        var tubeVolumeM3 = dimF * 0.001;

        var totalHeightMm = dimA + dimC;
        var totalHeightM = totalHeightMm * 0.001;

        var circleAreaM2 = Math.PI * radiusM * radiusM;

        // ============================================================
        // Проверяем физическую возможность трубного пучка.
        //
        // Объём труб не может превышать геометрический объём
        // цилиндрической области высотой dimD.
        // ============================================================

        var grossBundleRegionVolumeM3 = circleAreaM2 * bundleHeightM;

        if (tubeVolumeM3 > grossBundleRegionVolumeM3)
            return Type8VolumeDetails.Invalid(dimA, dimB, dimC, dimD, dimE, dimF, dimG);

        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);

        // ============================================================
        // Нижнее коническое / усечённо-коническое днище.
        //
        // h измеряется от нижнего малого основания вверх.
        //
        // rh =
        // r + (R - r) × h / C.
        //
        // Заполненная часть сама является усечённым конусом
        // с основаниями r и rh.
        //
        // При dimG = 0:
        //
        // r = 0
        //
        // и формула автоматически становится формулой конуса.
        //
        // При dimG = dimB:
        //
        // r = R
        //
        // и формула автоматически становится формулой цилиндра.
        // ============================================================

        var headFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);

        var headCurrentRadiusM = headHeightM > 0
            ? smallRadiusM + (radiusM - smallRadiusM) * headFillM / headHeightM
            : radiusM;

        var headVolumeM3 = CalculateFrustumVolume(smallRadiusM, headCurrentRadiusM, headFillM);
        var fullHeadVolumeM3 = CalculateFrustumVolume(smallRadiusM, radiusM, headHeightM);

        // ============================================================
        // Цилиндрическая часть.
        // ============================================================

        var bodyFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var grossBodyVolumeM3 = circleAreaM2 * bodyFillM;

        // ============================================================
        // Положение трубного пучка.
        //
        // Полностью повторяет Type 5.
        //
        // dimE измеряется сверху цилиндрической части.
        //
        // Координаты считаются от нижней точки всего Tank:
        //
        // bundleBottom = dimC + dimA - dimE - dimD
        // bundleTop    = dimC + dimA - dimE
        // ============================================================

        var bundleBottomM = headHeightM + bodyHeightM - upperSectionHeightM - bundleHeightM;
        var bundleTopM = headHeightM + bodyHeightM - upperSectionHeightM;

        // ============================================================
        // Погружённая часть труб.
        //
        // Вытесняющий объём возрастает линейно
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
            ? "Lower conical/frustum head"
            : liquidHeightM >= bundleBottomM && liquidHeightM <= bundleTopM
                ? "Cylindrical part / tube bundle"
                : "Cylindrical part";

        return new Type8VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD, DimEMm: dimE, DimFL: dimF, DimGMm: dimG,
            RadiusMm: radiusMm, RadiusM: radiusM, SmallRadiusMm: smallRadiusMm, SmallRadiusM: smallRadiusM,
            BodyHeightM: bodyHeightM, HeadHeightM: headHeightM, BundleHeightM: bundleHeightM,
            TotalHeightMm: totalHeightMm, TotalHeightM: totalHeightM,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            CircleAreaM2: circleAreaM2,
            HeadFillM: headFillM, HeadCurrentRadiusM: headCurrentRadiusM, HeadVolumeM3: headVolumeM3, FullHeadVolumeM3: fullHeadVolumeM3,
            BodyFillM: bodyFillM, GrossBodyVolumeM3: grossBodyVolumeM3,
            BundleBottomMm: bundleBottomM * 1000.0, BundleTopMm: bundleTopM * 1000.0, BundleFilledM: bundleFilledM,
            TubeVolumeM3: tubeVolumeM3, FloodedTubeVolumeM3: floodedTubeVolumeM3, GrossBundleRegionVolumeM3: grossBundleRegionVolumeM3,
            FullGrossVolumeM3: fullGrossVolumeM3, FullNetVolumeM3: fullNetVolumeM3,
            VolumeM3: volumeM3,
            CurrentRegion: currentRegion);
    }

    // ============================================================
    // Точный объём усечённого конуса.
    //
    // radius1M - радиус первого основания.
    // radius2M - радиус второго основания.
    // heightM  - высота между основаниями.
    //
    // Формула универсальна:
    //
    // radius1 = 0 -> настоящий конус.
    // radius1 = radius2 -> цилиндр.
    // 0 < radius1 < radius2 -> усечённый конус.
    // ============================================================
    private static double CalculateFrustumVolume(double radius1M, double radius2M, double heightM)
    {
        if (heightM <= 0)
            return 0.0;

        return Math.PI * heightM * (radius1M * radius1M + radius1M * radius2M + radius2M * radius2M) / 3.0;
    }

    private sealed record Type8VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm, double DimDMm, double DimEMm, double DimFL, double DimGMm,
        double RadiusMm, double RadiusM, double SmallRadiusMm, double SmallRadiusM,
        double BodyHeightM, double HeadHeightM, double BundleHeightM,
        double TotalHeightMm, double TotalHeightM,
        double LiquidHeightMm, double LiquidHeightM,
        double CircleAreaM2,
        double HeadFillM, double HeadCurrentRadiusM, double HeadVolumeM3, double FullHeadVolumeM3,
        double BodyFillM, double GrossBodyVolumeM3,
        double BundleBottomMm, double BundleTopMm, double BundleFilledM,
        double TubeVolumeM3, double FloodedTubeVolumeM3, double GrossBundleRegionVolumeM3,
        double FullGrossVolumeM3, double FullNetVolumeM3,
        double VolumeM3,
        string CurrentRegion)
    {
        public static Type8VolumeDetails Invalid(double dimA, double dimB, double dimC, double dimD, double dimE, double dimF, double dimG)
        {
            return new Type8VolumeDetails(
                dimA, dimB, dimC, dimD, dimE, dimF, dimG,
                0, 0, 0, 0,
                0, 0, 0,
                0, 0,
                0, 0,
                0,
                0, 0, double.NaN, double.NaN,
                0, double.NaN,
                0, 0, 0,
                0, 0, 0,
                double.NaN, double.NaN,
                double.NaN,
                "Invalid geometry");
        }
    }
}