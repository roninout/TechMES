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

        #region methods

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

                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            }
            else //Газ
            {
                //Плотность газа = P * 10^2/R/T(K)
                //R = 8.314
                //T(K) = t(Cels) + 273.15

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

            if (!this.isSteam)
            { //Жидкость
                //y = a2*x^2 + a1*x + a0
                a0 = 2.1864307;
                a1 = 0.0015649999;
                a2 = 0.0000083021163;                
            }
            else
            {//Газ

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

        //Расчет давления насыщенного пара при заданной температуре, бар, абс.
        private double GetPressure(double temperature)
        {
            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0

            double a0 = 0.036484162;
            double a1 = 0.0013598701;
            double a2 = 0.000067036419;
            double a3 = 0.000000064375591;
            double a4 = 8.6595042E-09;
            double a5 = 0.0;

            double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            
            return pressureSaturation;
        }

        /// <summary>
        /// Возвращает содержание ACN в указанной Content-системе, %.
        /// Для Acetonitrile основной production-корреляцией сейчас является исходная ContentCalc.ACN_Water_Content.
        ///
        /// Старый одиночный Acetonitrile.GetContent(T,P) больше не используется и полностью удалён.
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
        /// Основная Content-корреляция ACN в системе ACN + Water.
        /// Математика и коэффициенты переносятся 1:1 из ContentCalc.ACN_Water_Content.
        /// </summary>
        private static double CalculateAcnWaterContent(float temperature, float pressureBarAbsolute, int configurationCode)
        {
            //configurationCode = 10 - доазеотропная концентрация (колонна 1.Т04)
            //configurationCode = 20 - заазеотропная концентрация (колонна 1.Т05)

            //Определяем список давлений для расчета содержания
            List<double> lowPressureList = new List<double> { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.85, 0.90, 0.95, 1.0, 1.05, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0 }; //22
            List<double> highPressureList = new List<double> { 3.0, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 4.0, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 5.0 }; //21

            //Определяем коэффициенты для полинома при разных давлениях
            List<CoefSet> coefListLowPressure = new List<CoefSet>();  //Для давлений от 0.8bar до 1.5bar
            List<CoefSet> coefListHighPressure = new List<CoefSet>(); //Для давлений от 3.5bar до 4.5bar

            #region Coefs for Polynom for Low Pressure
            coefListLowPressure.Add(new CoefSet { a0 = 8.2013578000, a1 = -1.1992905000, a2 = 0.0786533790, a3 = -0.0025684228, a4 = 0.0000415490, a5 = -0.0000002696 });   //0
            coefListLowPressure.Add(new CoefSet { a0 = 63.5151230000, a1 = -6.8795964000, a2 = 0.3014546000, a3 = -0.0065831353, a4 = 0.0000716465, a5 = -0.0000003119 });  //1
            coefListLowPressure.Add(new CoefSet { a0 = 177.4142500000, a1 = -16.0329960000, a2 = 0.5811110200, a3 = -0.0105053830, a4 = 0.0000947512, a5 = -0.0000003417 });//2
            coefListLowPressure.Add(new CoefSet { a0 = 339.3460800000, a1 = -27.2247120000, a2 = 0.8742376400, a3 = -0.0140093180, a4 = 0.0001120576, a5 = -0.0000003584 });//3
            coefListLowPressure.Add(new CoefSet { a0 = 537.5884700000, a1 = -39.5307930000, a2 = 1.1627157000, a3 = -0.0170716290, a4 = 0.0001251547, a5 = -0.0000003668 });//4
            coefListLowPressure.Add(new CoefSet { a0 = 783.9303000000, a1 = -53.8488820000, a2 = 1.4790317000, a3 = -0.0202825380, a4 = 0.0001389015, a5 = -0.0000003803 });//5
            coefListLowPressure.Add(new CoefSet { a0 = 1070.1684000000, a1 = -69.5404230000, a2 = 1.8065416000, a3 = -0.0234347140, a4 = 0.0001518300, a5 = -0.0000003933 });//6


            coefListLowPressure.Add(new CoefSet { a0 = 1440.3777, a1 = -89.3228, a2 = 2.2141348, a3 = -0.027407457, a4 = 0.00016944558, a5 = -0.00000041877874 }); //7
            coefListLowPressure.Add(new CoefSet { a0 = 1570.0401, a1 = -95.33907, a2 = 2.3142095, a3 = -0.028054098, a4 = 0.00016987307, a5 = -0.00000041122021 }); //8
            coefListLowPressure.Add(new CoefSet { a0 = 1777.4775, a1 = -105.85585, a2 = 2.5198668, a3 = -0.029957834, a4 = 0.0001779023, a5 = -0.00000042233979 }); //9
            coefListLowPressure.Add(new CoefSet { a0 = 1896.2999, a1 = -110.86729, a2 = 2.5910406, a3 = -0.030244902, a4 = 0.00017636215, a5 = -0.00000041114958 }); //10
            coefListLowPressure.Add(new CoefSet { a0 = 2170.8347, a1 = -124.81087, a2 = 2.8682768, a3 = -0.032922187, a4 = 0.00018876302, a5 = -0.00000043266698 }); //11
            coefListLowPressure.Add(new CoefSet { a0 = 2217.0243, a1 = -125.26626, a2 = 2.8293404, a3 = -0.03192183, a4 = 0.00017993191, a5 = -0.00000040550801 }); //12
            coefListLowPressure.Add(new CoefSet { a0 = 2997.7116, a1 = -166.85622, a2 = 3.7115765, a3 = -0.04123259, a4 = 0.00022879164, a5 = -0.00000050742505 }); //13
            coefListLowPressure.Add(new CoefSet { a0 = 2794.7367, a1 = -151.15536, a2 = 3.2680989, a3 = -0.035298351, a4 = 0.00019048513, a5 = -0.00000041100391 }); //14
            coefListLowPressure.Add(new CoefSet { a0 = 3374.0552, a1 = -178.01867, a2 = 3.7544243, a3 = -0.039555216, a4 = 0.00020820833, a5 = -0.000000438164 }); //15
            coefListLowPressure.Add(new CoefSet { a0 = 4211.6383, a1 = -216.79478, a2 = 4.4604102, a3 = -0.045842584, a4 = 0.00023538451, a5 = -0.00000048316456 }); //16           
            coefListLowPressure.Add(new CoefSet { a0 = 4344.5903, a1 = -219.10347, a2 = 4.4169347, a3 = -0.044484198, a4 = 0.00022384557, a5 = -0.00000045034019 }); //17
            coefListLowPressure.Add(new CoefSet { a0 = 5081.7879, a1 = -251.39468, a2 = 4.9711015, a3 = -0.049108614, a4 = 0.00024238751, a5 = -0.00000047828714 }); //18 

            coefListLowPressure.Add(new CoefSet { a0 = 4767.4348, a1 = -230.90959, a2 = 4.4712516, a3 = -0.043261287, a4 = 0.00020917081, a5 = -0.00000040441435 }); //19
            coefListLowPressure.Add(new CoefSet { a0 = 8459.4208, a1 = -404.87347, a2 = 7.7440387, a3 = -0.073987313, a4 = 0.00035311011, a5 = -0.00000067354803 }); //20
            coefListLowPressure.Add(new CoefSet { a0 = 6969.8248, a1 = -327.1807, a2 = 6.1393161, a3 = -0.057555687, a4 = 0.0002696029, a5 = -0.00000050488142 }); //21
            coefListLowPressure.Add(new CoefSet { a0 = 4478.7845, a1 = -205.50501, a2 = 3.7710001, a3 = -0.034587978, a4 = 0.00015859715, a5 = -0.00000029092906 }); //22



            #endregion

            #region Coefs for Polynom for High Pressure
            //Правая часть таблицы содержаний T05


            coefListHighPressure.Add(new CoefSet { a0 = 87980.099, a1 = -3844.7658, a2 = 67.158348, a3 = -0.58613, a4 = 0.0025560129, a5 = -0.0000044556014 }); //0
            coefListHighPressure.Add(new CoefSet { a0 = -20532.252, a1 = 757.60717, a2 = -10.905136, a3 = 0.07576114, a4 = -0.00024940124, a5 = 0.00000029959645 }); //1
            coefListHighPressure.Add(new CoefSet { a0 = 3537.7347, a1 = -252.93777, a2 = 6.0170426, a3 = -0.065506198, a4 = 0.00033843212, a5 = -0.0000006756459 }); //2
            coefListHighPressure.Add(new CoefSet { a0 = 5808.4756, a1 = -334.30128, a2 = 7.1132805, a3 = -0.072146397, a4 = 0.00035430775, a5 = -0.00000068009355 }); //3
            coefListHighPressure.Add(new CoefSet { a0 = -1934.7189, a1 = -16.525458, a2 = 1.8714383, a3 = -0.028698368, a4 = 0.00017334152, a5 = -0.00000037709522 }); //4

            coefListHighPressure.Add(new CoefSet { a0 = -4623.7683, a1 = 101.39225, a2 = -0.21853782, a3 = -0.010028872, a4 = 0.000089477063, a5 = -0.00000022583619 }); //5
            coefListHighPressure.Add(new CoefSet { a0 = -6530.9838, a1 = 175.11241, a2 = -1.3803267, a3 = -0.00067773114, a4 = 0.000051001707, a5 = -0.00000016113319 }); //6
            coefListHighPressure.Add(new CoefSet { a0 = -5698.651, a1 = 142.12601, a2 = -0.88291432, a3 = -0.0042040401, a4 = 0.000062429435, a5 = -0.00000017370857 }); //7
            coefListHighPressure.Add(new CoefSet { a0 = -3366.7913, a1 = 49.531596, a2 = 0.56380186, a3 = -0.015307128, a4 = 0.00010418667, a5 = -0.00000023503169 }); //8
            coefListHighPressure.Add(new CoefSet { a0 = -5782.2063, a1 = 147.00324, a2 = -1.0307198, a3 = -0.0021042928, a4 = 0.000048935438, a5 = -0.00000014170429 }); //9
            coefListHighPressure.Add(new CoefSet { a0 = -5749.8125, a1 = 144.84776, a2 = -1.0030304, a3 = -0.0021140122, a4 = 0.000047525121, a5 = -0.00000013620981 }); //10
            coefListHighPressure.Add(new CoefSet { a0 = -1311.3499, a1 = -32.537868, a2 = 1.8109511, a3 = -0.02426936, a4 = 0.00013411085, a5 = -0.00000027058039 }); //11
            coefListHighPressure.Add(new CoefSet { a0 = -4882.7993, a1 = 103.90858, a2 = -0.29176898, a3 = -0.0079274534, a4 = 0.000070060799, a5 = -0.00000016932529 }); //12
            coefListHighPressure.Add(new CoefSet { a0 = -5179.9938, a1 = 116.64647, a2 = -0.5270818, a3 = -0.0056659286, a4 = 0.000059051373, a5 = -0.00000014794401 }); //13
            coefListHighPressure.Add(new CoefSet { a0 = -7811.578, a1 = 223.40104, a2 = -2.2721758, a3 = 0.0086797137, a4 = -0.00000016526893, a5 = -0.000000049879829 });//14 
            coefListHighPressure.Add(new CoefSet { a0 = -916.77916, a1 = -32.245825, a2 = 1.4999073, a3 = -0.01899434, a4 = 0.00010073274, a5 = -0.00000019603144 }); //15     

            coefListHighPressure.Add(new CoefSet { a0 = -325.70724, a1 = -63.954406, a2 = 2.1070016, a3 = -0.024443527, a4 = 0.0001241807, a5 = -0.0000002351911 }); //16
            coefListHighPressure.Add(new CoefSet { a0 = -5847.3739, a1 = 142.16109, a2 = -0.98418608, a3 = -0.0011614984, a4 = 0.000036123003, a5 = -0.00000010140856 });//17
            coefListHighPressure.Add(new CoefSet { a0 = -5642.0356, a1 = 133.65123, a2 = -0.85905124, a3 = -0.0019735528, a4 = 0.000038299316, a5 = -0.00000010279146 });//18
            coefListHighPressure.Add(new CoefSet { a0 = -1120.0791, a1 = -30.770929, a2 = 1.5162252, a3 = -0.019006605, a4 = 0.00009889065, a5 = -0.0000001882534 }); //19
            coefListHighPressure.Add(new CoefSet { a0 = -5386.3488, a1 = 126.39178, a2 = -0.81176117, a3 = -0.0016766359, a4 = 0.000034069792, a5 = -0.000000090820862 });//20



            //Левая часть таблицы содержаний T05
            //coefListHighPressure.Add(new CoefSet { a0 = -15540.734, a1 = 622.91925, a2 = -9.9689905, a3 = 0.07962754, a4 = -0.00031743681, a5 = 5.0523538e-007 }); //0
            //coefListHighPressure.Add(new CoefSet { a0 = -4064.9996, a1 = 166.6272, a2 = -2.7179244, a3 = 0.022063316, a4 = -8.9150054e-005, a5 = 1.4344192e-007 }); //1            
            //coefListHighPressure.Add(new CoefSet { a0 = -8217.1872, a1 = 327.68716, a2 = -5.2125024, a3 = 0.041348753, a4 = -0.00016357198, a5 = 2.5813121e-007 }); //2
            //coefListHighPressure.Add(new CoefSet { a0 = 22483.091, a1 = -863.49565, a2 = 13.258699, a3 = -0.10173687, a4 = 0.00039012815, a5 = -5.9814607e-007 }); //3
            //coefListHighPressure.Add(new CoefSet { a0 = 10844.666, a1 = -409.23559, a2 = 6.1761627, a3 = -0.046596765, a4 = 0.00017576418, a5 = -2.652234e-007 }); //4
            //coefListHighPressure.Add(new CoefSet { a0 = 6817.139, a1 = -252.27909, a2 = 3.7343077, a3 = -0.027637908, a4 = 0.00010229644, a5 = -1.5153786e-007 }); //5
            //coefListHighPressure.Add(new CoefSet { a0 = 17360.402, a1 = -650.3977, a2 = 9.7434126, a3 = -0.072955402, a4 = 0.00027304976, a5 = -4.0869368e-007 }); //6
            //coefListHighPressure.Add(new CoefSet { a0 = 3330.7822, a1 = -117.40557, a2 = 1.6532906, a3 = -0.011626555, a4 = 4.0852774e-005, a5 = -5.7434088e-008 }); //7
            //coefListHighPressure.Add(new CoefSet { a0 = 4370.1828, a1 = -155.19241, a2 = 2.2034065, a3 = -0.015635359, a4 = 5.5474057e-005, a5 = -7.8783641e-008 }); //8
            //coefListHighPressure.Add(new CoefSet { a0 = -9477.0398, a1 = 360.66645, a2 = -5.4752159, a3 = 0.041451305, a4 = -0.00015650377, a5 = 2.3573579e-007 }); //9 
            //coefListHighPressure.Add(new CoefSet { a0 = 5670.9176, a1 = -200.21628, a2 = 2.8264646, a3 = -0.01994419, a4 = 7.0364631e-005, a5 = -9.9351675e-008 }); //10     


            #endregion

            //Определяем по формулам какого давления (Low Pressure - Колонна Т04 или High Pressure - Колонна Т05) производим расчеты
            //var pressureList = _press < 2.9 ? lowPressureList : highPressureList;
            //var coefList = _press < 2.9 ? coefListLowPressure : coefListHighPressure;
            //12.07.2020 - ЗАмена признака перехода для расчета (правая часть графика - левая часть графика). Было давление, стало по конфигураионому коду

            List<double> pressureList = null;
            List<CoefSet> coefList = null;

            //Левая часть графика
            if (configurationCode / 10 == 1) //configurationCode == 10
            {
                pressureList = lowPressureList;
                coefList = coefListLowPressure;
            }

            if (configurationCode / 10 == 2) //configurationCode == 20
            {
                pressureList = highPressureList;
                coefList = coefListHighPressure;
            }

            if (pressureList == null || coefList == null)
                return 0.0;

            //Определяем номер формулы (по давлению - линейная интерполяция)
            var numOfRange = GetNumOfFormula(pressureList, pressureBarAbsolute, out double deviation);

            double content;
            //Вичисляем содержание
            //Если переданное давление ниже минимального в массиве -
            if (numOfRange == 0)
            {
                //Считаем по формуле №0
                //content = getPolynomValue(_temp, coefList[0]);

                //01.04.2020 - Считаем по коэффициенту наклона прямой вниз влево
                var y1 = GetPolynomValue(temperature, coefList[0]);
                var y2 = GetPolynomValue(temperature, coefList[1]);

                content = y1 - (y2 - Math.Abs(y1)) * (pressureList[0] - pressureBarAbsolute) / (pressureList[1] - pressureList[0]);
            }

            //Если переданное давление - больше максимального в массиве - 
            else if (numOfRange == pressureList.Count)
            {
                //Считаем по формуле №pressureList.Count - 1
                //content = getPolynomValue(_temp, coefList[pressureList.Count - 1]);

                //01.04.2020 - Считаем по коэффициенту наклона прямой вниз влево
                var y1 = GetPolynomValue(temperature, coefList[pressureList.Count - 2]);
                var y2 = GetPolynomValue(temperature, coefList[pressureList.Count - 1]);
                content = y2 + (y2 - Math.Abs(y1)) * (pressureBarAbsolute - pressureList[pressureList.Count - 1]) / (pressureList[pressureList.Count - 1] - pressureList[pressureList.Count - 2]);
            }


            //Если попали в точку базового давления-
            else if (1 - deviation < 0.1)
                //Считаем по конкретной формуле один раз
                content = GetPolynomValue(temperature, coefList[numOfRange]);

            else
            {
                //Считем по двум формулам
                double tmpcount_1 = GetPolynomValue(temperature, coefList[numOfRange - 1]);
                double tmpcount_2 = GetPolynomValue(temperature, coefList[numOfRange]);
                content = tmpcount_1 + (tmpcount_2 - tmpcount_1) * deviation;
            }

            return Math.Max(0.0, Math.Min(100.0, content * 100.0));
        }

        #endregion

    }
}
