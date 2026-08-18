using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// TYPE 1.
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
/// Volume рассчитывается по точной геометрии эллипсоида.
/// Эмпирические коэффициенты 0.848 больше не используются.
/// </summary>
public sealed class TankType1VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10),
        Dimension("dimB", "dimB", 11),
        Dimension("dimC", "dimC", 12));

    public override string Code => "tank.volume.type1";

    public override string Name => "Type 1 — vertical, two elliptical heads";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        return parameters.GetRequiredDouble("dimA") + parameters.GetRequiredDouble("dimC") * 2.0;
    }


    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        /*
         * В Base compatibility layer параметр levelMm
         * уже содержит физическую высоту жидкости от самого дна Tank.
         */
        var liquidHeightMm = parameters.GetRequiredDouble("levelMm");
        var dimA = parameters.GetRequiredDouble("dimA");
        var dimB = parameters.GetRequiredDouble("dimB");
        var dimC = parameters.GetRequiredDouble("dimC");

        if (dimA < 0 || dimB <= 0 || dimC < 0)
            return double.NaN;

        var radius = dimB * 0.0005;
        var bodyHeight = dimA * 0.001;
        var headHeight = dimC * 0.001;
        var totalHeight = bodyHeight + headHeight * 2.0;
        var liquidHeight = Math.Clamp(liquidHeightMm * 0.001, 0.0, totalHeight);

        var circleArea = Math.PI * radius * radius;

        /*
         * Если dimC = 0, получаем обычный цилиндр
         * с плоскими верхом и низом.
         */
        if (headHeight <= 0)
            return circleArea * Math.Min(liquidHeight, bodyHeight);

        /*
         * Полный объём одного полуэллипсоидного днища:
         *
         * V = 2/3 * PI * R² * C
         */
        var fullHeadVolume = 2.0 * Math.PI * radius * radius * headHeight / 3.0;

        /*
         * Нижнее эллиптическое днище.
         *
         * Интеграл площади горизонтального сечения
         * полуэллипсоида от нижней точки до высоты h:
         *
         * V(h) = PI * R² * (h²/C - h³/(3*C²))
         */
        if (liquidHeight <= headHeight)
        {
            return Math.PI * radius * radius *
                   (liquidHeight * liquidHeight / headHeight
                    - Math.Pow(liquidHeight, 3.0) / (3.0 * headHeight * headHeight));
        }

        /*
         * Цилиндрическая часть.
         */
        if (liquidHeight <= headHeight + bodyHeight)
        {
            var bodyLiquidHeight = liquidHeight - headHeight;
            return fullHeadVolume + circleArea * bodyLiquidHeight;
        }

        /*
         * Верхнее эллиптическое днище.
         *
         * x - заполнение верхнего днища от его основания.
         *
         * V(x) = PI * R² * (x - x³/(3*C²))
         */
        var upperHeadLevel = liquidHeight - headHeight - bodyHeight;

        var upperHeadVolume = Math.PI * radius * radius *
                              (upperHeadLevel
                               - Math.Pow(upperHeadLevel, 3.0) / (3.0 * headHeight * headHeight));

        return fullHeadVolume + circleArea * bodyHeight + upperHeadVolume;
    }
}