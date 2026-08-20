using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 3
//
// Вертикальный цилиндрический Tank с нижним эллиптическим днищем и вертикальной перегородкой.
//
// dimA - высота цилиндрической части, mm.
// dimB - внутренний диаметр Tank, mm.
// dimC - высота нижнего эллиптического днища, mm.
// dimD - расстояние от левой стенки до перегородки, mm.
//
// Рассчитываем объём отсека слева от перегородки.
//
// Старые distanceA / distanceB / distToDistanceA / probeLength и эмпирический коэффициент 0.85 здесь больше не используются.
// ============================================================
public sealed class TankType3VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private const int HeadIntegrationSteps = 1024;

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
            Dimension("dimA", "dimA", 10, minimum: 1d),
            Dimension("dimB", "dimB", 11, minimum: 1d),
            Dimension("dimC", "dimC", 12),
            Dimension("dimD", "dimD", 13, minimum: 1d));

    public override string Code => "tank.volume.type3";
    public override string Name => "Type 3 — vertical with partition";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Полная физическая высота Tank:
    // нижнее эллиптическое днище + цилиндрическая часть.
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
    // Все промежуточные вычисления Type 3 находятся здесь.
    // WEB только отображает Trace и не знает формул.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            new("help.geometry.radius.formula", "Radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.total-height.formula", "Total height formula", "Htotal = dimC + dimA"),
            new("help.geometry.total-height.calculation", "Total height calculation", $"{F(d.DimCMm)} + {F(d.DimAMm)} = {F(d.TotalHeightMm)} mm"),

            new("help.geometry.partition.formula", "Partition coordinate formula", "xP = dimD - R"),
            new("help.geometry.partition.calculation", "Partition coordinate", $"{F(d.DimDMm)} - {F(d.RadiusMm)} = {F(d.PartitionXmm)} mm"),

            // ============================================================
            // Cylindrical compartment
            // ============================================================

            new("help.volume.body-segment.formula", "Compartment cross-section formula",
                "Asegment = R² × acos(-xP / R) + xP × sqrt(R² - xP²)"),

            new("help.volume.body-segment.calculation", "Compartment cross-section",
                $"Asegment = {F(d.BodySegmentAreaM2)} m²"),

            // ============================================================
            // Lower elliptical head
            // ============================================================

            new("help.volume.head-radius.formula", "Elliptical head slice radius",
                "r(z) = R × sqrt(1 - ((z - C) / C)²)"),

            new("help.volume.head-slice.formula", "Elliptical head slice area",
                "Ahead(z) = Asegment(r(z), xP)"),

            new("help.volume.head-integral.formula", "Lower head volume formula",
                "Vhead(h) = ∫₀^min(h,C) Ahead(z) dz"),

            new("help.volume.head-integral.calculation", "Current lower head volume",
                $"hHead = {F(d.HeadFillM)} m → Vhead = {F(d.HeadVolumeM3)} m³"),

            new("help.volume.full-head.formula", "Full compartment head formula",
                "VheadFull = π × R × C / 2 × (xP - xP³ / (3R²) + 2R / 3)"),

            new("help.volume.full-head.calculation", "Full lower head compartment volume",
                $"VheadFull = {F(d.FullHeadCompartmentVolumeM3)} m³"),

            // ============================================================
            // Cylindrical body
            // ============================================================

            new("help.volume.body-height.formula", "Current cylindrical height",
                "Hbody = clamp(Hliquid - dimC, 0, dimA)"),

            new("help.volume.body-height.calculation", "Current cylindrical height",
                $"Hbody = {F(d.BodyFillM)} m"),

            new("help.volume.body.formula", "Current cylindrical volume formula",
                "Vbody = Asegment × Hbody"),

            new("help.volume.body.calculation", "Current cylindrical volume",
                $"{F(d.BodySegmentAreaM2)} × {F(d.BodyFillM)} = {F(d.BodyVolumeM3)} m³"),

            // ============================================================
            // Full compartment
            // ============================================================

            new("help.volume.full-body.calculation", "Full cylindrical compartment volume",
                $"{F(d.BodySegmentAreaM2)} × {F(d.BodyHeightM)} = {F(d.FullBodyVolumeM3)} m³"),

            new("help.volume.full-tank.formula", "Full compartment formula",
                "Vfull = VheadFull + VbodyFull"),

            new("help.volume.full-tank.calculation", "Full compartment volume",
                $"{F(d.FullHeadCompartmentVolumeM3)} + {F(d.FullBodyVolumeM3)} = {F(d.FullCompartmentVolumeM3)} m³"),

            // ============================================================
            // Current result
            // ============================================================

            new("help.volume.current.region", "Current liquid region", d.CurrentRegion),

            new("help.result.volume.formula", "Current Volume formula",
                "Volume = Vhead + Vbody"),

            new("help.result.volume.calculation", "Current calculated Volume",
                $"{F(d.HeadVolumeM3)} + {F(d.BodyVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Основной расчёт Type 3.
    //
    // Один CalculateDetails используется и Runtime-расчётом,
    // и Help. Отдельной копии математики нет.
    // ============================================================
    private static Type3VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");

        // dimD физически находится внутри диаметра Tank.
        if (dimA <= 0 || dimB <= 0 || dimC < 0 || dimD <= 0 || dimD > dimB)
            return Type3VolumeDetails.Invalid(dimA, dimB, dimC, dimD);

        var bodyHeightM = dimA * 0.001;
        var diameterM = dimB * 0.001;
        var radiusM = diameterM / 2.0;
        var radiusMm = dimB / 2.0;
        var headHeightM = dimC * 0.001;
        var totalHeightM = bodyHeightM + headHeightM;
        var totalHeightMm = dimA + dimC;

        // Начало координат по X находится в центре Tank.
        // Левая стенка = -R.
        // Поэтому координата перегородки:
        //
        // xP = -R + dimD.
        var partitionXM = dimD * 0.001 - radiusM;
        var partitionXmm = dimD - radiusMm;

        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeightM);

        // Площадь цилиндрического отсека постоянна по всей dimA.
        var bodySegmentAreaM2 = CalculateCircularAreaLeftOfPartition(radiusM, partitionXM);

        // Нижнее эллиптическое днище.
        var headFillM = Math.Clamp(liquidHeightM, 0.0, headHeightM);
        var headVolumeM3 = CalculatePartialLowerHeadVolume(radiusM, headHeightM, partitionXM, headFillM);

        // Цилиндрическая часть начинается выше dimC.
        var bodyFillM = Math.Clamp(liquidHeightM - headHeightM, 0.0, bodyHeightM);
        var bodyVolumeM3 = bodySegmentAreaM2 * bodyFillM;

        var fullHeadCompartmentVolumeM3 = CalculateFullLowerHeadVolume(radiusM, headHeightM, partitionXM);
        var fullBodyVolumeM3 = bodySegmentAreaM2 * bodyHeightM;
        var fullCompartmentVolumeM3 = fullHeadCompartmentVolumeM3 + fullBodyVolumeM3;
        var volumeM3 = headVolumeM3 + bodyVolumeM3;

        var currentRegion = headHeightM > 0 && liquidHeightM <= headHeightM
            ? "Lower elliptical head"
            : "Cylindrical compartment";

        return new Type3VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD,
            RadiusMm: radiusMm, RadiusM: radiusM,
            HeadHeightM: headHeightM, BodyHeightM: bodyHeightM, TotalHeightMm: totalHeightMm,
            PartitionXmm: partitionXmm, PartitionXM: partitionXM,
            BodySegmentAreaM2: bodySegmentAreaM2,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            HeadFillM: headFillM, BodyFillM: bodyFillM,
            HeadVolumeM3: headVolumeM3, BodyVolumeM3: bodyVolumeM3,
            FullHeadCompartmentVolumeM3: fullHeadCompartmentVolumeM3,
            FullBodyVolumeM3: fullBodyVolumeM3,
            FullCompartmentVolumeM3: fullCompartmentVolumeM3,
            VolumeM3: volumeM3,
            CurrentRegion: currentRegion);
    }

    // ============================================================
    // Площадь части круга слева от вертикальной перегородки.
    //
    // partitionX:
    // -R -> площадь 0
    //  0 -> половина круга
    // +R -> полный круг
    // ============================================================
    private static double CalculateCircularAreaLeftOfPartition(double radiusM, double partitionXM)
    {
        if (radiusM <= 0 || partitionXM <= -radiusM)
            return 0.0;

        if (partitionXM >= radiusM)
            return Math.PI * radiusM * radiusM;

        var root = Math.Sqrt(Math.Max(0.0, radiusM * radiusM - partitionXM * partitionXM));
        return radiusM * radiusM * Math.Acos(-partitionXM / radiusM) + partitionXM * root;
    }

    // ============================================================
    // Полный объём части нижнего полуэллипсоида,
    // находящейся слева от вертикальной перегородки.
    //
    // Формула получена интегрированием полного полуэллипсоида
    // относительно координаты X.
    // ============================================================
    private static double CalculateFullLowerHeadVolume(double radiusM, double headHeightM, double partitionXM)
    {
        if (radiusM <= 0 || headHeightM <= 0 || partitionXM <= -radiusM)
            return 0.0;

        if (partitionXM >= radiusM)
            return 2.0 * Math.PI * radiusM * radiusM * headHeightM / 3.0;

        return Math.PI * radiusM * headHeightM / 2.0 *
            (partitionXM - partitionXM * partitionXM * partitionXM / (3.0 * radiusM * radiusM) + 2.0 * radiusM / 3.0);
    }

    // ============================================================
    // Частичный объём нижнего эллиптического днища.
    //
    // На каждой высоте z получаем горизонтальный круг
    // с радиусом:
    //
    // r(z) = R * sqrt(1 - ((z-C)/C)^2)
    //
    // После этого берём только площадь слева от перегородки.
    //
    // Полученный точный геометрический интеграл вычисляем
    // методом Simpson. Никакого старого коэффициента 0.85 нет.
    // ============================================================
    private static double CalculatePartialLowerHeadVolume(double radiusM, double headHeightM, double partitionXM, double fillHeightM)
    {
        if (radiusM <= 0 || headHeightM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= headHeightM)
            return CalculateFullLowerHeadVolume(radiusM, headHeightM, partitionXM);

        double SliceArea(double z)
        {
            var normalized = (z - headHeightM) / headHeightM;
            var sliceRadius = radiusM * Math.Sqrt(Math.Max(0.0, 1.0 - normalized * normalized));
            return CalculateCircularAreaLeftOfPartition(sliceRadius, partitionXM);
        }

        return IntegrateSimpson(SliceArea, 0.0, fillHeightM, HeadIntegrationSteps);
    }

    // Метод Simpson используется только когда уровень находится внутри
    // нижнего эллиптического днища. Выше dimC используется закрытая
    // формула полного объёма днища, поэтому Runtime не тратит время
    // на интегрирование при обычных рабочих уровнях.
    private static double IntegrateSimpson(Func<double, double> function, double from, double to, int steps)
    {
        if (to <= from)
            return 0.0;

        if (steps < 2)
            steps = 2;

        if ((steps & 1) != 0)
            steps++;

        var step = (to - from) / steps;
        var sum = function(from) + function(to);

        for (var i = 1; i < steps; i++)
            sum += function(from + step * i) * (i % 2 == 0 ? 2.0 : 4.0);

        return sum * step / 3.0;
    }

    private sealed record Type3VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm, double DimDMm,
        double RadiusMm, double RadiusM,
        double HeadHeightM, double BodyHeightM, double TotalHeightMm,
        double PartitionXmm, double PartitionXM,
        double BodySegmentAreaM2,
        double LiquidHeightMm, double LiquidHeightM,
        double HeadFillM, double BodyFillM,
        double HeadVolumeM3, double BodyVolumeM3,
        double FullHeadCompartmentVolumeM3,
        double FullBodyVolumeM3,
        double FullCompartmentVolumeM3,
        double VolumeM3,
        string CurrentRegion)
    {
        public static Type3VolumeDetails Invalid(double dimA, double dimB, double dimC, double dimD)
        {
            return new Type3VolumeDetails(
                dimA, dimB, dimC, dimD,
                0, 0,
                0, 0, 0,
                0, 0,
                0,
                0, 0,
                0, 0,
                0, 0,
                0, 0, 0,
                double.NaN,
                "Invalid geometry");
        }
    }
}