using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 7
// Горизонтальный Tank:
//
// левый конический / усечённо-конический торец
// +
// горизонтальная цилиндрическая часть
// +
// правый конический / усечённо-конический торец.
//
// dimA - длина цилиндрической части, mm.
// dimB - внутренний диаметр цилиндрической части, mm.
// dimC - осевая длина одного конического торца, mm.
// dimD - диаметр малого основания конического торца, mm.
//
// Уровень измеряется вертикально. Поэтому полная высота Tank по направлению Level:
// Htotal = dimB.
//
// Оба торца соосны основной цилиндрической части.
// При: dimD = 0 получаем два настоящих конуса.
// При: dimD = dimB конические торцы превращаются в цилиндрические продолжения.
// ============================================================
public sealed class TankType7VolumeDefinition : TankTypeVolumeDefinitionBase
{
    // Частичный объём усечённого конуса определяется интегрированием точной площади кругового сегмента по оси торца.
    // 4096 интервалов Simpson дают точность намного выше, чем требуется для инженерного результата Volume в m³.
    // Это параметр численного интегрирования, а не коэффициент геометрической модели.
    private const int ConicalEndIntegrationSteps = 4096;

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10, minimum: 1d),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12, minimum: 0d),
        Dimension("dimD", "dimD", 13, minimum: 0d));

    public override string Code => "tank.volume.type7";
    public override string Name => "Type 7 — horizontal, two conical ends";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Для горизонтального Tank датчик уровня работает по вертикальному диаметру основной части.
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimB");
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        return CalculateDetails(parameters, liquidHeightMm).VolumeM3;
    }

    // ============================================================
    // HELP использует тот же CalculateDetails(), что и настоящий Runtime-расчёт.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            // ============================================================
            // Main geometry
            // ============================================================

            new("help.geometry.radius.formula", "Main radius formula", "R = dimB / 2"),
            new("help.geometry.radius.calculation", "Main radius calculation", $"R = {F(d.DimBMm)} / 2 = {F(d.RadiusMm)} mm = {F(d.RadiusM)} m"),

            new("help.geometry.small-radius.formula", "Small end radius formula", "r = dimD / 2"),
            new("help.geometry.small-radius.calculation", "Small end radius calculation", $"r = {F(d.DimDMm)} / 2 = {F(d.SmallRadiusMm)} mm = {F(d.SmallRadiusM)} m"),

            new("help.geometry.axial-length.formula", "Total axial length formula", "Ltotal = dimA + 2 × dimC"),
            new("help.geometry.axial-length.calculation", "Total axial length calculation", $"{F(d.DimAMm)} + 2 × {F(d.DimCMm)} = {F(d.TotalAxialLengthMm)} mm"),

            new("help.geometry.liquid-height", "Physical liquid height", $"h = {F(d.LiquidHeightMm)} mm = {F(d.LiquidHeightM)} m"),

            // ============================================================
            // Horizontal cylinder
            // ============================================================

            new("help.volume.segment-angle.formula", "Circular segment angle", "θ = 2 × acos((R - h) / R)"),
            new("help.volume.segment-angle.calculation", "Circular segment angle calculation", $"θ = {F(d.SegmentAngleRad)} rad"),

            new("help.volume.segment.formula", "Circular segment area formula", "Asegment = R² / 2 × (θ - sin θ)"),
            new("help.volume.segment.calculation", "Current circular segment area", $"{F(d.RadiusM)}² / 2 × ({F(d.SegmentAngleRad)} - sin({F(d.SegmentAngleRad)})) = {F(d.SegmentAreaM2)} m²"),

            new("help.volume.cylinder.formula", "Current cylindrical volume formula", "Vcyl = Asegment × dimA"),
            new("help.volume.cylinder.calculation", "Current cylindrical volume", $"{F(d.SegmentAreaM2)} × {F(d.CylinderLengthM)} = {F(d.CylinderVolumeM3)} m³"),

            // ============================================================
            // Conical end geometry
            //
            // x = 0      -> соединение с цилиндром, radius = R.
            // x = dimC   -> малое основание, radius = r.
            // ============================================================

            new("help.volume.end-radius.formula", "Conical end local radius", "ρ(x) = R - (R - r) × x / C"),
            new("help.volume.end-fill.formula", "Local liquid depth", "d(x) = h - (R - ρ(x))"),

            // Для каждого x получаем обычный круг радиуса ρ(x),
            // но его нижняя точка находится выше общего дна Tank
            // на величину R - ρ(x).
            new("help.volume.end-section.formula", "Local segment area", "A(x) = circularSegmentArea(ρ(x), d(x))"),

            // Частичный объём торца нельзя корректно заменить
            // equivalent cylinder length.
            //
            // Интегрируем реальные локальные круговые сегменты.
            new("help.volume.end.formula", "One conical end volume", "Vend = ∫₀ᶜ A(x) dx"),
            new("help.volume.end.calculation", "Current volume of one conical end", $"Numerical integration of exact circular segments = {F(d.OneEndVolumeM3)} m³"),

            new("help.volume.ends.formula", "Two conical ends formula", "Vends = 2 × Vend"),
            new("help.volume.ends.calculation", "Current volume of two conical ends", $"2 × {F(d.OneEndVolumeM3)} = {F(d.TwoEndsVolumeM3)} m³"),

            // ============================================================
            // Full geometry
            // ============================================================

            new("help.volume.full-cylinder.formula", "Full cylinder formula", "VcylFull = π × R² × dimA"),
            new("help.volume.full-cylinder.calculation", "Full cylindrical volume", $"π × {F(d.RadiusM)}² × {F(d.CylinderLengthM)} = {F(d.FullCylinderVolumeM3)} m³"),

            // Точный объём усечённого конуса:
            //
            // V = πC/3 × (R² + Rr + r²).
            new("help.volume.full-end.formula", "Full conical end formula", "VendFull = π × C / 3 × (R² + R × r + r²)"),
            new("help.volume.full-end.calculation", "Full volume of one conical end", $"π × {F(d.HeadLengthM)} / 3 × ({F(d.RadiusM)}² + {F(d.RadiusM)} × {F(d.SmallRadiusM)} + {F(d.SmallRadiusM)}²) = {F(d.FullOneEndVolumeM3)} m³"),

            new("help.volume.full-tank.formula", "Full Tank formula", "Vfull = VcylFull + 2 × VendFull"),
            new("help.volume.full-tank.calculation", "Full Tank volume", $"{F(d.FullCylinderVolumeM3)} + 2 × {F(d.FullOneEndVolumeM3)} = {F(d.FullTankVolumeM3)} m³"),

            // ============================================================
            // Result
            // ============================================================

            new("help.result.volume.formula", "Current Volume formula", "Volume = Vcyl + 2 × Vend"),
            new("help.result.volume.calculation", "Current calculated Volume", $"{F(d.CylinderVolumeM3)} + 2 × {F(d.OneEndVolumeM3)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Единственный внутренний расчёт Type 7.
    // ============================================================
    private static Type7VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");
        var dimD = parameters.GetRequiredDouble("dimD");

        // Малое основание не может быть больше
        // диаметра основной цилиндрической части.
        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || !double.IsFinite(dimD)
            || dimA <= 0 || dimB <= 0 || dimC < 0 || dimD < 0 || dimD > dimB)
        {
            return Type7VolumeDetails.Invalid(dimA, dimB, dimC, dimD);
        }

        var cylinderLengthM = dimA * 0.001;
        var diameterM = dimB * 0.001;
        var radiusM = diameterM / 2.0;
        var radiusMm = dimB / 2.0;

        var headLengthM = dimC * 0.001;
        var smallRadiusM = dimD * 0.001 / 2.0;
        var smallRadiusMm = dimD / 2.0;

        var liquidHeightM = Math.Clamp(liquidHeightMm * 0.001, 0.0, diameterM);

        // ============================================================
        // Main horizontal cylinder
        // ============================================================

        var segmentAngleRad = CalculateSegmentAngle(radiusM, liquidHeightM);
        var segmentAreaM2 = CalculateCircularSegmentArea(radiusM, liquidHeightM);
        var cylinderVolumeM3 = segmentAreaM2 * cylinderLengthM;

        // ============================================================
        // Two exact conical / frustum ends
        // ============================================================

        var oneEndVolumeM3 = CalculateOneConicalEndVolume(radiusM, smallRadiusM, headLengthM, liquidHeightM);
        var twoEndsVolumeM3 = oneEndVolumeM3 * 2.0;

        // ============================================================
        // Full reference geometry
        // ============================================================

        var fullCylinderVolumeM3 = Math.PI * radiusM * radiusM * cylinderLengthM;

        var fullOneEndVolumeM3 = headLengthM > 0.0
            ? Math.PI * headLengthM * (radiusM * radiusM + radiusM * smallRadiusM + smallRadiusM * smallRadiusM) / 3.0
            : 0.0;

        var fullTankVolumeM3 = fullCylinderVolumeM3 + fullOneEndVolumeM3 * 2.0;
        var volumeM3 = cylinderVolumeM3 + twoEndsVolumeM3;

        return new Type7VolumeDetails(
            DimAMm: dimA, DimBMm: dimB, DimCMm: dimC, DimDMm: dimD,
            RadiusMm: radiusMm, RadiusM: radiusM,
            SmallRadiusMm: smallRadiusMm, SmallRadiusM: smallRadiusM,
            CylinderLengthM: cylinderLengthM, HeadLengthM: headLengthM,
            TotalAxialLengthMm: dimA + dimC * 2.0,
            LiquidHeightMm: liquidHeightM * 1000.0, LiquidHeightM: liquidHeightM,
            SegmentAngleRad: segmentAngleRad, SegmentAreaM2: segmentAreaM2,
            CylinderVolumeM3: cylinderVolumeM3,
            OneEndVolumeM3: oneEndVolumeM3, TwoEndsVolumeM3: twoEndsVolumeM3,
            FullCylinderVolumeM3: fullCylinderVolumeM3,
            FullOneEndVolumeM3: fullOneEndVolumeM3,
            FullTankVolumeM3: fullTankVolumeM3,
            VolumeM3: volumeM3);
    }

    // ============================================================
    // Центральный угол заполненного сегмента основного круга.
    // ============================================================
    private static double CalculateSegmentAngle(double radiusM, double fillHeightM)
    {
        if (radiusM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= radiusM * 2.0)
            return Math.PI * 2.0;

        var argument = Math.Clamp((radiusM - fillHeightM) / radiusM, -1.0, 1.0);
        return 2.0 * Math.Acos(argument);
    }

    // ============================================================
    // Точная площадь кругового сегмента.
    //
    // fillHeightM измеряется от нижней точки данного круга.
    //
    // Для заполнения выше центра используем симметрию:
    // A(h) = πR² - A(2R - h)
    // Это также уменьшает численную потерю точности.
    // ============================================================
    private static double CalculateCircularSegmentArea(double radiusM, double fillHeightM)
    {
        if (radiusM <= 0 || fillHeightM <= 0)
            return 0.0;

        if (fillHeightM >= radiusM * 2.0)
            return Math.PI * radiusM * radiusM;

        if (fillHeightM > radiusM)
            return Math.PI * radiusM * radiusM - CalculateCircularSegmentArea(radiusM, radiusM * 2.0 - fillHeightM);

        var argument = Math.Clamp((radiusM - fillHeightM) / radiusM, -1.0, 1.0);
        var theta = 2.0 * Math.Acos(argument);

        return radiusM * radiusM * (theta - Math.Sin(theta)) / 2.0;
    }

    // ============================================================
    // Частичный объём ОДНОГО конического торца.
    //
    // Торец является телом вращения вокруг горизонтальной оси.
    //
    // В точке x:
    //
    // localRadius =
    // R - (R - r) × x / C.
    //
    // Центры всех локальных окружностей лежат
    // на одной горизонтальной оси.
    //
    // Поэтому нижняя точка локального круга поднята
    // относительно общего дна Tank на:
    //
    // R - localRadius.
    //
    // Локальная глубина жидкости:
    //
    // localFill =
    // liquidHeight - (R - localRadius).
    //
    // После этого используем обычную точную
    // площадь кругового сегмента и интегрируем по x.
    // ============================================================
    private static double CalculateOneConicalEndVolume(double mainRadiusM, double smallRadiusM, double headLengthM, double liquidHeightM)
    {
        if (mainRadiusM <= 0 || headLengthM <= 0 || liquidHeightM <= 0)
            return 0.0;

        var fullVolumeM3 = Math.PI * headLengthM * (mainRadiusM * mainRadiusM + mainRadiusM * smallRadiusM + smallRadiusM * smallRadiusM) / 3.0;
        var diameterM = mainRadiusM * 2.0;
        var h = Math.Clamp(liquidHeightM, 0.0, diameterM);

        if (h <= 0.0)
            return 0.0;

        if (h >= diameterM)
            return fullVolumeM3;

        // Геометрия симметрична относительно горизонтальной оси.
        //
        // Поэтому верхнюю половину считаем через дополнение
        // до полного объёма. Это повышает численную устойчивость.
        if (h > mainRadiusM)
            return fullVolumeM3 - CalculateOneConicalEndVolume(mainRadiusM, smallRadiusM, headLengthM, diameterM - h);

        // При уровне точно по оси любая соосная окружность
        // заполнена ровно наполовину.
        if (Math.Abs(h - mainRadiusM) <= 1e-12)
            return fullVolumeM3 / 2.0;

        // Если малое основание равно основному диаметру,
        // торец вырождается в обычный цилиндр длиной dimC.
        if (Math.Abs(mainRadiusM - smallRadiusM) <= 1e-12)
            return CalculateCircularSegmentArea(mainRadiusM, h) * headLengthM;

        // ============================================================
        // Simpson integration.
        //
        // Для каждого x вычисляем настоящий localRadius,
        // настоящий localFill и точный circular segment area.
        // ============================================================

        double SectionArea(double x)
        {
            var localRadiusM = mainRadiusM + (smallRadiusM - mainRadiusM) * x / headLengthM;

            if (localRadiusM <= 0.0)
                return 0.0;

            var localBottomElevationM = mainRadiusM - localRadiusM;
            var localFillM = h - localBottomElevationM;

            return CalculateCircularSegmentArea(localRadiusM, localFillM);
        }

        var stepM = headLengthM / ConicalEndIntegrationSteps;
        var sum = SectionArea(0.0) + SectionArea(headLengthM);

        for (var i = 1; i < ConicalEndIntegrationSteps; i++)
        {
            var x = i * stepM;
            sum += (i % 2 == 0 ? 2.0 : 4.0) * SectionArea(x);
        }

        return sum * stepM / 3.0;
    }

    private sealed record Type7VolumeDetails(
        double DimAMm, double DimBMm, double DimCMm, double DimDMm,
        double RadiusMm, double RadiusM,
        double SmallRadiusMm, double SmallRadiusM,
        double CylinderLengthM, double HeadLengthM,
        double TotalAxialLengthMm,
        double LiquidHeightMm, double LiquidHeightM,
        double SegmentAngleRad, double SegmentAreaM2,
        double CylinderVolumeM3,
        double OneEndVolumeM3, double TwoEndsVolumeM3,
        double FullCylinderVolumeM3,
        double FullOneEndVolumeM3,
        double FullTankVolumeM3,
        double VolumeM3)
    {
        public static Type7VolumeDetails Invalid(double dimA, double dimB, double dimC, double dimD)
        {
            return new Type7VolumeDetails(
                dimA, dimB, dimC, dimD,
                0, 0,
                0, 0,
                0, 0,
                0,
                0, 0,
                0, 0,
                double.NaN,
                double.NaN, double.NaN,
                double.NaN,
                double.NaN,
                double.NaN,
                double.NaN);
        }
    }
}