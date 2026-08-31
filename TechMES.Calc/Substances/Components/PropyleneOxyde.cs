using TechMES.Calc.Content;
using TechMES.Calc.Exceptions;
using static TechMES.Calc.Content.ContentCorrelationMath;

namespace TechMES.Calc.Substances.Components;

internal class PropyleneOxyde : LegacySubstance, IContentSubstanceModel
{
    #region fields & props

    private const double molarMass = 58.08;

    // Молярная масса пропиленоксида.
    public override double MolarMass => molarMass;

    // Признак агрегатного состояния пропиленоксида в точке измерения.
    public override bool IsSteam => isSteam;

    #endregion

    public PropyleneOxyde(bool _isSteam) : base(_isSteam)
    {
    }

    #region Density / Capacity

    // Метод для определения плотности вещества при 100% концентрации, кг/м3.
    public override double GetDensity(float temperature, float pressure)
    {
        double a0 = 0.0;
        double a1 = 0.0;
        double a2 = 0.0;
        double a3 = 0.0;
        double a4 = 0.0;
        double a5 = 0.0;

        double density = 0.0;

        if (!isSteam)
        {
            a0 = 853.7;
            a1 = -1.22;

            density =
                a5 * Math.Pow(temperature, 5) +
                a4 * Math.Pow(temperature, 4) +
                a3 * Math.Pow(temperature, 3) +
                a2 * Math.Pow(temperature, 2) +
                a1 * temperature +
                a0;
        }
        else
        {
            try
            {
                density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
            }
            catch (ArithmeticException)
            {
            }
        }

        return density;
    }

    // Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК.
    public override double GetCapacity(float temperature)
    {
        double a0;
        double a1;
        double a2;
        double a3;
        double a4;
        double a5;

        if (!isSteam)
        {
            a0 = 2.1013073;
            a1 = 0.0037279583;
            a2 = 0.000011584685;
            a3 = 6.1272975E-15;
            a4 = -2.4889982E-16;
            a5 = 1.5252912E-18;
        }
        else
        {
            a0 = 1.1479922;
            a1 = 0.0039040574;
            a2 = -0.0000027020205;
            a3 = 7.9984491E-10;
            a4 = -5.1017917E-17;
            a5 = 4.2568435E-19;
        }

        return
            a5 * Math.Pow(temperature, 5) +
            a4 * Math.Pow(temperature, 4) +
            a3 * Math.Pow(temperature, 3) +
            a2 * Math.Pow(temperature, 2) +
            a1 * temperature +
            a0;
    }

    // Расчет давления насыщенного пара при заданной температуре, bar(abs).
    private double GetPressure(double temperature)
    {
        double a0 = 0.24433327;
        double a1 = 0.011605649;
        double a2 = 0.00022534828;
        double a3 = 0.0000021758871;
        double a4 = 8.3126655E-09;
        double a5 = 0.0;

        return
            a5 * Math.Pow(temperature, 5) +
            a4 * Math.Pow(temperature, 4) +
            a3 * Math.Pow(temperature, 3) +
            a2 * Math.Pow(temperature, 2) +
            a1 * temperature +
            a0;
    }

    #endregion

    #region Content

    /// <summary>
    /// Возвращает содержание PO в указанной Content-системе, %.
    ///
    /// Формулы перенесены из:
    /// ContentCalc.PO_P_Content
    /// ContentCalc.PO_Water_Content
    ///
    /// Наружу сразу возвращаются инженерные проценты 0..100,
    /// без старого SCADA scaling 0..10000.
    /// </summary>
    public double GetContent(float temperature, float pressureBarAbsolute, ContentSystem system, int configurationCode)
    {
        if (isSteam)
            throw new CalculationException("content.phase.unsupported", "PO Content correlations are defined only for liquid Propylene oxide.");

        return system switch
        {
            ContentSystem.PoPropylene => CalculatePoPropyleneContent(temperature, pressureBarAbsolute, configurationCode),
            ContentSystem.PoWater => CalculatePoWaterContent(temperature, pressureBarAbsolute, configurationCode),

            _ => throw new CalculationException("content.system.unsupported", $"Propylene oxide Content correlation is not defined for system '{system}'.")
        };
    }

    /// <summary>
    /// PO + Propylene.
    ///
    /// Математика перенесена 1:1 из ContentCalc.PO_P_Content.
    /// </summary>
    private static double CalculatePoPropyleneContent(float temperature, float pressureBarAbsolute, int configurationCode)
    {
        List<double> pressureList =
        [
            1.0,
            1.3,
            1.6,
            1.9,
            2.0,
            2.2,
            2.5
        ];

        List<CoefSet> coefList =
        [
            new() { a0 = 0.27015091, a1 = 0.013587132, a2 = 0.00022833994, a3 = 0.00000080966815, a4 = -0.000000018938742, a5 = -0.00000000018159973 },
            new() { a0 = 0.19911062, a1 = 0.010672565, a2 = 0.00019342888, a3 = 0.00000099556486, a4 = -0.000000011842013, a5 = -0.00000000014420463 },
            new() { a0 = 0.15316527, a1 = 0.0087154892, a2 = 0.00016672003, a3 = 0.0000010343219, a4 = -0.0000000076751746, a5 = -0.0000000001183291 },
            new() { a0 = 0.12100836, a1 = 0.0073119613, a2 = 0.00014603952, a3 = 0.0000010179438, a4 = -0.0000000050740111, a5 = -0.000000000099363734 },
            new() { a0 = 0.1123328, a1 = 0.0069282581, a2 = 0.00014016119, a3 = 0.0000010073206, a4 = -0.000000004420446, a5 = -0.000000000094334086 },
            new() { a0 = 0.097244104, a1 = 0.0062562339, a2 = 0.00012966385, a3 = 0.00000098138507, a4 = -0.0000000033762105, a5 = -0.000000000084930748 },
            new() { a0 = 0.078967415, a1 = 0.0054329712, a2 = 0.00011640761, a3 = 0.00000093950915, a4 = -0.0000000022392263, a5 = -0.00000000007364854 }
        ];

        var numOfRange = GetNumOfFormula(pressureList, pressureBarAbsolute, out double deviation);

        double content;

        if (numOfRange == 0)
        {
            content = GetPolynomValue(temperature, coefList[0]);
        }
        else if (numOfRange == pressureList.Count)
        {
            content = GetPolynomValue(temperature, coefList[pressureList.Count - 1]);
        }
        else if (1 - deviation < 0.1)
        {
            content = GetPolynomValue(temperature, coefList[numOfRange]);
        }
        else
        {
            double tmpcount1 = GetPolynomValue(temperature, coefList[numOfRange - 1]);
            double tmpcount2 = GetPolynomValue(temperature, coefList[numOfRange]);

            // В legacy при увеличении давления содержание снижалось.
            content = tmpcount1 - (tmpcount1 - tmpcount2) * deviation;
        }

        // В legacy: content * 10000 -> hundredths of percent.
        //
        // Здесь сразу возвращаем обычные проценты.
        if (configurationCode % 10 == 1)
            return content * 100.0;

        return Math.Max(0.0, Math.Min(100.0, content * 100.0));
    }

    /// <summary>
    /// PO + Water.
    ///
    /// Математика перенесена 1:1 из ContentCalc.PO_Water_Content.
    /// </summary>
    private static double CalculatePoWaterContent(float temperature, float pressureBarAbsolute, int configurationCode)
    {
        List<double> pressureList =
        [
            0.5,
            0.6,
            0.7,
            0.8,
            0.9,
            1.0,
            1.1,
            1.2,
            1.3,
            1.4,
            1.5,
            1.6,
            1.7,
            1.8,
            1.9,
            2.0
        ];

        List<CoefSet> coefList =
        [
            new() { a0 = 2.7093011, a1 = -0.23408704, a2 = 0.011829591, a3 = -0.00028176147, a4 = 0.0000031854242, a5 = -0.000000013946172 },
            new() { a0 = 3.8409595, a1 = -0.33718629, a2 = 0.015089884, a3 = -0.00032249033, a4 = 0.000003307335, a5 = -0.000000013215794 },
            new() { a0 = 5.1863059, a1 = -0.44747144, a2 = 0.018253765, a3 = -0.0003584722, a4 = 0.0000034019083, a5 = -0.0000000126303 },
            new() { a0 = 6.7006878, a1 = -0.56099571, a2 = 0.021240062, a3 = -0.00038935005, a4 = 0.0000034661094, a5 = -0.000000012107901 },
            new() { a0 = 8.4029747, a1 = -0.68052377, a2 = 0.024203475, a3 = -0.00041850558, a4 = 0.0000035273071, a5 = -0.000000011692387 },
            new() { a0 = 10.249351, a1 = -0.8027264, a2 = 0.027065198, a3 = -0.00044505426, a4 = 0.0000035773567, a5 = -0.000000011329427 },
            new() { a0 = 12.247331, a1 = -0.92868304, a2 = 0.029884817, a3 = -0.00047018964, a4 = 0.0000036242812, a5 = -0.000000011022925 },
            new() { a0 = 14.373864, a1 = -1.0570257, a2 = 0.032640794, a3 = -0.00049379049, a4 = 0.0000036664236, a5 = -0.000000010754359 },
            new() { a0 = 16.588707, a1 = -1.1850395, a2 = 0.035269933, a3 = -0.00051512742, a4 = 0.0000036983444, a5 = -0.000000010499794 },
            new() { a0 = 18.927772, a1 = -1.3157837, a2 = 0.037873998, a3 = -0.00053573842, a4 = 0.00000373, a5 = -0.000000010278392 },
            new() { a0 = 21.381486, a1 = -1.4487946, a2 = 0.040448736, a3 = -0.00055562662, a4 = 0.0000037608837, a5 = -0.000000010082724 },
            new() { a0 = 23.928099, a1 = -1.5828794, a2 = 0.042972152, a3 = -0.00057458663, a4 = 0.0000037893714, a5 = -0.0000000099046818 },
            new() { a0 = 26.531277, a1 = -1.715805, a2 = 0.045393961, a3 = -0.00059205033, a4 = 0.0000038117998, a5 = -0.0000000097322613 },
            new() { a0 = 29.259985, a1 = -1.8522268, a2 = 0.047836867, a3 = -0.0006095565, a4 = 0.0000038370845, a5 = -0.0000000095834988 },
            new() { a0 = 32.069815, a1 = -1.9895125, a2 = 0.050241304, a3 = -0.0006264238, a4 = 0.0000038609954, a5 = -0.0000000094463455 },
            new() { a0 = 34.921566, a1 = -2.1254708, a2 = 0.052560794, a3 = -0.000642155, a4 = 0.0000038805979, a5 = -0.0000000093125595 }
        ];

        var numOfRange = GetNumOfFormula(pressureList, pressureBarAbsolute, out double deviation);

        double content;

        if (numOfRange == 0)
        {
            content = GetPolynomValue(temperature, coefList[0]);
        }
        else if (numOfRange == pressureList.Count)
        {
            content = GetPolynomValue(temperature, coefList[pressureList.Count - 1]);
        }
        else if (1 - deviation < 0.1)
        {
            content = GetPolynomValue(temperature, coefList[numOfRange]);
        }
        else
        {
            double tmpcount1 = GetPolynomValue(temperature, coefList[numOfRange - 1]);
            double tmpcount2 = GetPolynomValue(temperature, coefList[numOfRange]);
            content = tmpcount1 - (tmpcount1 - tmpcount2) * deviation;
        }

        if (configurationCode % 10 == 1)
            return content * 100.0;

        return Math.Max(0.0, Math.Min(100.0, content * 100.0));
    }

    #endregion
}