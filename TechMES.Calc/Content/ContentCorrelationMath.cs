namespace TechMES.Calc.Content;

/// <summary>
/// Общая математическая инфраструктура legacy Content-корреляций.
///
/// Здесь нет физической формулы конкретного вещества.
/// Класс содержит только общий выбор диапазона давления
/// и вычисление полинома по коэффициентам.
/// </summary>
internal static class ContentCorrelationMath
{
    /// <summary>
    /// Определяет индекс ближайшей правой реперной точки давления
    /// и относительное положение текущего давления между точками.
    ///
    /// Логика перенесена из ContentCalc без изменения математики.
    /// </summary>
    public static int GetNumOfFormula(IReadOnlyList<double> pressures, double pressure, out double deviation)
    {
        int numOfRange;

        for (numOfRange = 0; numOfRange < pressures.Count; numOfRange++)
        {
            if (pressures[numOfRange] >= pressure)
            {
                if (numOfRange == 0)
                    deviation = 0.0;
                else
                    deviation = 1.0 - ((pressures[numOfRange] - pressures[numOfRange - 1]) - (pressure - pressures[numOfRange - 1])) / (pressures[numOfRange] - pressures[numOfRange - 1]);

                return numOfRange;
            }
        }

        deviation = 0.0;

        return numOfRange;
    }

    /// <summary>
    /// Возвращает значение legacy-полинома пятой степени.
    /// </summary>
    public static double GetPolynomValue(float temperature, CoefSet coefSet)
    {
        return coefSet.a5 * Math.Pow(temperature, 5) + coefSet.a4 * Math.Pow(temperature, 4) + coefSet.a3 * Math.Pow(temperature, 3) + coefSet.a2 * Math.Pow(temperature, 2) + coefSet.a1 * temperature + coefSet.a0;
    }
}

/// <summary>
/// Набор коэффициентов legacy Content-полинома.
///
/// Имена полей намеренно сохранены такими же, как в ContentCalc,
/// чтобы перенос таблиц коэффициентов был механическим и без риска
/// переставить коэффициенты местами.
/// </summary>
internal struct CoefSet
{
    public double a0;
    public double a1;
    public double a2;
    public double a3;
    public double a4;
    public double a5;
}