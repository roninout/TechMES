using TechMES.Calc.Content;
using TechMES.Calc.Exceptions;
using static TechMES.Calc.Content.ContentCorrelationMath;

namespace TechMES.Calc.Substances.Components
{
    internal class Acetonitrile : LegacySubstance, IContentSubstanceModel
    {
        
        #region fields & props

        private const double molarMass = 41.0524;        

        //Молярная масса ацетонитрила
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния ацетонитрила в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Acetonitrile(bool _isSteam) : base(_isSteam)
        {
            
        }

        #region Density / Capacity

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            double a0 = 0.0;
            double a1 = 0.0;
            double a2 = 0.0;
            double a3 = 0.0;
            double a4 = 0.0;
            double a5 = 0.0;

            double density = 0.0;

            if (!this.isSteam) //Жидкость
            {               
                a0 = 803.07;
                a1 = -1.0542;

                density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            }
            else //Газ
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

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
            double a0 = 0.0;
            double a1 = 0.0;
            double a2 = 0.0;
            double a3 = 0.0;
            double a4 = 0.0;
            double a5 = 0.0;

            double capacity = 0.0;

            if (!this.isSteam) //Жидкость
            { 
                a0 = 2.1864307;
                a1 = 0.0015649999;
                a2 = 0.0000083021163;                
            }
            else //Газ
            {

                a0 = 1.2125728;
                a1 = 0.0022147106;
                a2 = 0.0000024869344;
                a3 = -0.000000025107206;
                a4 = 5.9195896E-11;
                a5 = 0.0;                
            }

            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        #endregion

        #region Content

        private static readonly double[] AcnWaterLowPressures =
        [
            0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8,
            0.85, 0.90, 0.95, 1.0, 1.05, 1.1, 1.2, 1.3,
            1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0
        ];

        private static readonly double[] AcnWaterHighPressures =
        [
            3.0, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6,
            3.7, 3.8, 3.9, 4.0, 4.1, 4.2, 4.3,
            4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 5.0
        ];

        /// <summary>
        /// Коэффициенты левой части ACN-Water корреляции.
        /// configurationCode / 10 == 1.
        /// </summary>
        private static readonly CoefSet[] AcnWaterLowCoefs =
        [
            new() { a0 = 8.2013578000, a1 = -1.1992905000, a2 = 0.0786533790, a3 = -0.0025684228, a4 = 0.0000415490, a5 = -0.0000002696 },
            new() { a0 = 63.5151230000, a1 = -6.8795964000, a2 = 0.3014546000, a3 = -0.0065831353, a4 = 0.0000716465, a5 = -0.0000003119 },
            new() { a0 = 177.4142500000, a1 = -16.0329960000, a2 = 0.5811110200, a3 = -0.0105053830, a4 = 0.0000947512, a5 = -0.0000003417 },
            new() { a0 = 339.3460800000, a1 = -27.2247120000, a2 = 0.8742376400, a3 = -0.0140093180, a4 = 0.0001120576, a5 = -0.0000003584 },
            new() { a0 = 537.5884700000, a1 = -39.5307930000, a2 = 1.1627157000, a3 = -0.0170716290, a4 = 0.0001251547, a5 = -0.0000003668 },
            new() { a0 = 783.9303000000, a1 = -53.8488820000, a2 = 1.4790317000, a3 = -0.0202825380, a4 = 0.0001389015, a5 = -0.0000003803 },
            new() { a0 = 1070.1684000000, a1 = -69.5404230000, a2 = 1.8065416000, a3 = -0.0234347140, a4 = 0.0001518300, a5 = -0.0000003933 },
            new() { a0 = 1440.3777, a1 = -89.3228, a2 = 2.2141348, a3 = -0.027407457, a4 = 0.00016944558, a5 = -0.00000041877874 },
            new() { a0 = 1570.0401, a1 = -95.33907, a2 = 2.3142095, a3 = -0.028054098, a4 = 0.00016987307, a5 = -0.00000041122021 },
            new() { a0 = 1777.4775, a1 = -105.85585, a2 = 2.5198668, a3 = -0.029957834, a4 = 0.0001779023, a5 = -0.00000042233979 },
            new() { a0 = 1896.2999, a1 = -110.86729, a2 = 2.5910406, a3 = -0.030244902, a4 = 0.00017636215, a5 = -0.00000041114958 },
            new() { a0 = 2170.8347, a1 = -124.81087, a2 = 2.8682768, a3 = -0.032922187, a4 = 0.00018876302, a5 = -0.00000043266698 },
            new() { a0 = 2217.0243, a1 = -125.26626, a2 = 2.8293404, a3 = -0.03192183, a4 = 0.00017993191, a5 = -0.00000040550801 },
            new() { a0 = 2997.7116, a1 = -166.85622, a2 = 3.7115765, a3 = -0.04123259, a4 = 0.00022879164, a5 = -0.00000050742505 },
            new() { a0 = 2794.7367, a1 = -151.15536, a2 = 3.2680989, a3 = -0.035298351, a4 = 0.00019048513, a5 = -0.00000041100391 },
            new() { a0 = 3374.0552, a1 = -178.01867, a2 = 3.7544243, a3 = -0.039555216, a4 = 0.00020820833, a5 = -0.000000438164 },
            new() { a0 = 4211.6383, a1 = -216.79478, a2 = 4.4604102, a3 = -0.045842584, a4 = 0.00023538451, a5 = -0.00000048316456 },
            new() { a0 = 4344.5903, a1 = -219.10347, a2 = 4.4169347, a3 = -0.044484198, a4 = 0.00022384557, a5 = -0.00000045034019 },
            new() { a0 = 5081.7879, a1 = -251.39468, a2 = 4.9711015, a3 = -0.049108614, a4 = 0.00024238751, a5 = -0.00000047828714 },
            new() { a0 = 4767.4348, a1 = -230.90959, a2 = 4.4712516, a3 = -0.043261287, a4 = 0.00020917081, a5 = -0.00000040441435 },
            new() { a0 = 8459.4208, a1 = -404.87347, a2 = 7.7440387, a3 = -0.073987313, a4 = 0.00035311011, a5 = -0.00000067354803 },
            new() { a0 = 6969.8248, a1 = -327.1807, a2 = 6.1393161, a3 = -0.057555687, a4 = 0.0002696029, a5 = -0.00000050488142 },
            new() { a0 = 4478.7845, a1 = -205.50501, a2 = 3.7710001, a3 = -0.034587978, a4 = 0.00015859715, a5 = -0.00000029092906 }
        ];

        /// <summary>
        /// Коэффициенты правой части ACN-Water корреляции.
        /// configurationCode / 10 == 2.
        /// </summary>
        private static readonly CoefSet[] AcnWaterHighCoefs =
        [
            new() { a0 = 87980.099, a1 = -3844.7658, a2 = 67.158348, a3 = -0.58613, a4 = 0.0025560129, a5 = -0.0000044556014 },
            new() { a0 = -20532.252, a1 = 757.60717, a2 = -10.905136, a3 = 0.07576114, a4 = -0.00024940124, a5 = 0.00000029959645 },
            new() { a0 = 3537.7347, a1 = -252.93777, a2 = 6.0170426, a3 = -0.065506198, a4 = 0.00033843212, a5 = -0.0000006756459 },
            new() { a0 = 5808.4756, a1 = -334.30128, a2 = 7.1132805, a3 = -0.072146397, a4 = 0.00035430775, a5 = -0.00000068009355 },
            new() { a0 = -1934.7189, a1 = -16.525458, a2 = 1.8714383, a3 = -0.028698368, a4 = 0.00017334152, a5 = -0.00000037709522 },
            new() { a0 = -4623.7683, a1 = 101.39225, a2 = -0.21853782, a3 = -0.010028872, a4 = 0.000089477063, a5 = -0.00000022583619 },
            new() { a0 = -6530.9838, a1 = 175.11241, a2 = -1.3803267, a3 = -0.00067773114, a4 = 0.000051001707, a5 = -0.00000016113319 },
            new() { a0 = -5698.651, a1 = 142.12601, a2 = -0.88291432, a3 = -0.0042040401, a4 = 0.000062429435, a5 = -0.00000017370857 },
            new() { a0 = -3366.7913, a1 = 49.531596, a2 = 0.56380186, a3 = -0.015307128, a4 = 0.00010418667, a5 = -0.00000023503169 },
            new() { a0 = -5782.2063, a1 = 147.00324, a2 = -1.0307198, a3 = -0.0021042928, a4 = 0.000048935438, a5 = -0.00000014170429 },
            new() { a0 = -5749.8125, a1 = 144.84776, a2 = -1.0030304, a3 = -0.0021140122, a4 = 0.000047525121, a5 = -0.00000013620981 },
            new() { a0 = -1311.3499, a1 = -32.537868, a2 = 1.8109511, a3 = -0.02426936, a4 = 0.00013411085, a5 = -0.00000027058039 },
            new() { a0 = -4882.7993, a1 = 103.90858, a2 = -0.29176898, a3 = -0.0079274534, a4 = 0.000070060799, a5 = -0.00000016932529 },
            new() { a0 = -5179.9938, a1 = 116.64647, a2 = -0.5270818, a3 = -0.0056659286, a4 = 0.000059051373, a5 = -0.00000014794401 },
            new() { a0 = -7811.578, a1 = 223.40104, a2 = -2.2721758, a3 = 0.0086797137, a4 = -0.00000016526893, a5 = -0.000000049879829 },
            new() { a0 = -916.77916, a1 = -32.245825, a2 = 1.4999073, a3 = -0.01899434, a4 = 0.00010073274, a5 = -0.00000019603144 },
            new() { a0 = -325.70724, a1 = -63.954406, a2 = 2.1070016, a3 = -0.024443527, a4 = 0.0001241807, a5 = -0.0000002351911 },
            new() { a0 = -5847.3739, a1 = 142.16109, a2 = -0.98418608, a3 = -0.0011614984, a4 = 0.000036123003, a5 = -0.00000010140856 },
            new() { a0 = -5642.0356, a1 = 133.65123, a2 = -0.85905124, a3 = -0.0019735528, a4 = 0.000038299316, a5 = -0.00000010279146 },
            new() { a0 = -1120.0791, a1 = -30.770929, a2 = 1.5162252, a3 = -0.019006605, a4 = 0.00009889065, a5 = -0.0000001882534 },
            new() { a0 = -5386.3488, a1 = 126.39178, a2 = -0.81176117, a3 = -0.0016766359, a4 = 0.000034069792, a5 = -0.000000090820862 }
        ];

        /// <summary>
        /// Возвращает содержание ACN в системе ACN + Water, %.
        /// </summary>
        public double GetContent(float temperature, float pressureBarAbsolute, ContentSystem system, int configurationCode)
        {
            if (isSteam)
                throw new CalculationException("content.phase.unsupported", "ACN Content correlation is defined only for liquid Acetonitrile.");

            if (system != ContentSystem.AcnWater)
                throw new CalculationException("content.system.unsupported", $"Acetonitrile Content correlation is not defined for system '{system}'.");

            if (configurationCode / 10 is not 1 and not 2)
                throw new CalculationException("content.configuration.unsupported", $"ACN-Water Content configuration '{configurationCode}' is not supported.");

            return CalculateAcnWaterContent(temperature, pressureBarAbsolute, configurationCode);
        }

        /// <summary>
        /// ACN + Water.
        ///
        /// Вся математика ниже сохранена из legacy-корреляции.
        /// Особое внимание: экстраполяция ниже и выше диапазона использует Math.Abs(y1). Эту часть не упрощать.
        /// </summary>
        private static double CalculateAcnWaterContent(float temperature, float pressureBarAbsolute, int configurationCode)
        {
            double[] pressureList;
            CoefSet[] coefList;

            switch (configurationCode / 10)
            {
                case 1:
                    pressureList = AcnWaterLowPressures;
                    coefList = AcnWaterLowCoefs;
                    break;

                case 2:
                    pressureList = AcnWaterHighPressures;
                    coefList = AcnWaterHighCoefs;
                    break;

                default:
                    return 0.0;
            }

            var numOfRange = GetNumOfFormula(pressureList, pressureBarAbsolute, out double deviation);
            double content;

            // Ниже минимального давления.
            // Legacy-формула намеренно сохранена без изменений.
            if (numOfRange == 0)
            {
                var y1 = GetPolynomValue(temperature, coefList[0]);
                var y2 = GetPolynomValue(temperature, coefList[1]);
                content = y1 - (y2 - Math.Abs(y1)) * (pressureList[0] - pressureBarAbsolute) / (pressureList[1] - pressureList[0]);
            }

            // Выше максимального давления.
            // Legacy-формула намеренно сохранена без изменений.
            else if (numOfRange == pressureList.Length)
            {
                var y1 = GetPolynomValue(temperature, coefList[^2]);
                var y2 = GetPolynomValue(temperature, coefList[^1]);
                content = y2 + (y2 - Math.Abs(y1)) * (pressureBarAbsolute - pressureList[^1]) / (pressureList[^1] - pressureList[^2]);
            }

            // Реперная точка.
            else if (1 - deviation < 0.1)
            {
                content = GetPolynomValue(temperature, coefList[numOfRange]);
            }

            // Интерполяция.
            else
            {
                var tmpCount1 = GetPolynomValue(temperature, coefList[numOfRange - 1]);
                var tmpCount2 = GetPolynomValue(temperature, coefList[numOfRange]);
                content = tmpCount1 + (tmpCount2 - tmpCount1) * deviation;
            }

            return Math.Max(0.0, Math.Min(100.0, content * 100.0));
        }

        #endregion

    }
}
