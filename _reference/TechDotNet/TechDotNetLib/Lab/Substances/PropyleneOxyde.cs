using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class PropyleneOxyde : Substance
    {
        #region fields & props

        private const double molarMass = 58.08;

        //Молярная масса пропиленоксида
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния пропиленоксида в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public PropyleneOxyde(bool _isSteam) : base(_isSteam)
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
                a0 = 853.7;
                a1 = -1.22;

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
                a0 = 2.1013073;
                a1 = 0.0037279583;
                a2 = 0.000011584685;
                a3 = 6.1272975E-15;
                a4 = -2.4889982E-16;
                a5 = 1.5252912E-18;

            }
            else
            {//Газ

                a0 = 1.1479922;
                a1 = 0.0039040574;
                a2 = -0.0000027020205;
                a3 = 7.9984491E-10;
                a4 = -5.1017917E-17;
                a5 = 4.2568435E-19;
            }

            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        //Расчет давления насыщенного пара при заданной температуре, бар, абс.
        private double GetPressure(double temperature)
        {
            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0

            double a0 = 0.24433327;
            double a1 = 0.011605649;
            double a2 = 0.00022534828;
            double a3 = 0.0000021758871;
            double a4 = 8.3126655E-09;
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
