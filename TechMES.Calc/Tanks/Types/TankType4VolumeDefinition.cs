using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

// ============================================================
// TYPE 4
//
// Прямоугольный вертикальный Tank / параллелепипед.
//
// dimA - полная внутренняя высота Tank, mm.
// dimB - внутренняя ширина Tank, mm.
// dimC - внутренняя глубина Tank, mm.
//
// У Tank постоянная площадь горизонтального сечения:
//
// S = dimB × dimC
//
// Поэтому объём линейно зависит от физической высоты жидкости:
//
// V = S × Hliquid
//
// Старые distanceA / distanceB / distToDistanceA / probeLength
// в расчёте Type 4 больше не используются.
// ============================================================
public sealed class TankType4VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10, minimum: 1d),
        Dimension("dimB", "dimB", 11, minimum: 1d),
        Dimension("dimC", "dimC", 12, minimum: 1d));

    public override string Code => "tank.volume.type4";
    public override string Name => "Type 4 — rectangular tank";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    // Для вертикального прямоугольного Tank направление
    // измерения уровня совпадает с dimA.
    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimA");
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        return CalculateDetails(parameters, liquidHeightMm).VolumeM3;
    }

    // ============================================================
    // HELP
    //
    // Формулы и реальные промежуточные значения Type 4
    // находятся в самом алгоритме.
    //
    // WEB только отображает Trace и не содержит своей копии
    // математических формул.
    // ============================================================
    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var d = CalculateDetails(parameters, liquidHeightMm);

        return
        [
            new("help.geometry.height.formula", "Tank height", "Htank = dimA"),
            new("help.geometry.height.calculation", "Tank height calculation", $"Htank = {F(d.DimAMm)} mm = {F(d.HeightM)} m"),

            new("help.geometry.width.formula", "Tank width", "W = dimB"),
            new("help.geometry.width.calculation", "Tank width calculation", $"W = {F(d.DimBMm)} mm = {F(d.WidthM)} m"),

            new("help.geometry.depth.formula", "Tank depth", "D = dimC"),
            new("help.geometry.depth.calculation", "Tank depth calculation", $"D = {F(d.DimCMm)} mm = {F(d.DepthM)} m"),

            // ============================================================
            // Постоянная площадь горизонтального сечения.
            // ============================================================

            new("help.volume.base-area.formula", "Base area formula", "Sbase = W × D"),
            new("help.volume.base-area.calculation", "Base area calculation", $"{F(d.WidthM)} × {F(d.DepthM)} = {F(d.BaseAreaM2)} m²"),

            // ============================================================
            // Полный объём.
            // ============================================================

            new("help.volume.full.formula", "Full Tank volume formula", "Vfull = Sbase × Htank"),
            new("help.volume.full.calculation", "Full Tank volume calculation", $"{F(d.BaseAreaM2)} × {F(d.HeightM)} = {F(d.FullVolumeM3)} m³"),

            // ============================================================
            // Текущий объём.
            // liquidHeightMm уже является физической высотой жидкости,
            // рассчитанной общей Sensor-логикой Base.
            // ============================================================

            new("help.volume.liquid-height.formula", "Liquid height", "Hliquid = current physical liquid height"),
            new("help.volume.liquid-height.calculation", "Current liquid height", $"Hliquid = {F(d.LiquidHeightMm)} mm = {F(d.LiquidHeightM)} m"),

            new("help.result.volume.formula", "Current Volume formula", "Volume = Sbase × Hliquid"),
            new("help.result.volume.calculation", "Current calculated Volume", $"{F(d.BaseAreaM2)} × {F(d.LiquidHeightM)} = {F(d.VolumeM3)} m³")
        ];
    }

    // ============================================================
    // Единственный геометрический расчёт Type 4.
    //
    // Он используется одновременно Runtime-расчётом и Help,
    // поэтому отдельной копии формул для интерфейса нет.
    // ============================================================
    private static Type4VolumeDetails CalculateDetails(CalculationParameterSet parameters, double liquidHeightMm)
    {
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");

        if (!double.IsFinite(dimA) || !double.IsFinite(dimB) || !double.IsFinite(dimC) || dimA <= 0 || dimB <= 0 || dimC <= 0)
            return Type4VolumeDetails.Invalid(dimA, dimB, dimC);

        var heightM = dimA * 0.001;
        var widthM = dimB * 0.001;
        var depthM = dimC * 0.001;
        var liquidHeightMmClamped = Math.Clamp(liquidHeightMm, 0.0, dimA);
        var liquidHeightM = liquidHeightMmClamped * 0.001;

        var baseAreaM2 = widthM * depthM;
        var fullVolumeM3 = baseAreaM2 * heightM;
        var volumeM3 = baseAreaM2 * liquidHeightM;

        return new Type4VolumeDetails(
            DimAMm: dimA,
            DimBMm: dimB,
            DimCMm: dimC,
            HeightM: heightM,
            WidthM: widthM,
            DepthM: depthM,
            BaseAreaM2: baseAreaM2,
            LiquidHeightMm: liquidHeightMmClamped,
            LiquidHeightM: liquidHeightM,
            FullVolumeM3: fullVolumeM3,
            VolumeM3: volumeM3);
    }

    private sealed record Type4VolumeDetails(
        double DimAMm,
        double DimBMm,
        double DimCMm,
        double HeightM,
        double WidthM,
        double DepthM,
        double BaseAreaM2,
        double LiquidHeightMm,
        double LiquidHeightM,
        double FullVolumeM3,
        double VolumeM3)
    {
        public static Type4VolumeDetails Invalid(double dimA, double dimB, double dimC)
        {
            return new Type4VolumeDetails(dimA, dimB, dimC, 0, 0, 0, 0, 0, 0, 0, double.NaN);
        }
    }
}