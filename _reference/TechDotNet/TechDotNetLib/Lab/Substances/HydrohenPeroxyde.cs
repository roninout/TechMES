using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class HydrohenPeroxyde : Substance
    {
        #region fields & props
        private const double molarMass = 34.015;

        //Молярная масса перекиси водорода
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния перекиси водорода в точке измерения
        public override bool IsSteam => isSteam;

        #endregion


        public HydrohenPeroxyde(bool _isSteam) : base(_isSteam)
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
                a0 = 1471.4234;
                a1 = -1.1229705;
                a2 = -0.00043327967;
                a3 = -0.00000072845085;

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
                a0 = 2.4605939;
                a1 = 0.0021372924;
                a2 = 0.0;
                a3 = 0.0;
                a4 = 0.0;
                a5 = 0.0;

            }
            else
            {//Газ

                a0 = 1.2117451;
                a1 = 0.0011298187;
                a2 = 0.0000024125834;
                a3 = -0.000000016911386;
                a4 = 3.1232139E-11;
                a5 = 0.0;
            }
            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        //Расчет давления насыщенного пара при заданной температуре, бар, абс.
        private double GetPressure(double temperature)
        {
            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0

            double a0 = 1.310908;
            double a1 = -0.038738568;
            double a2 = 0.00047857264;
            double a3 = -0.0000029883632;
            double a4 = 9.5284911E-09;
            double a5 = 0.0;

            double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;

            return pressureSaturation;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1;
        }

        #endregion
    }
}
