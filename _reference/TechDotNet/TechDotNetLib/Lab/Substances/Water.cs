using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TechDotNetLib.Lab.Substances.WaterSteemProLib;

namespace TechDotNetLib.Lab.Substances
{
    internal class Water : Substance
    {
        #region fields & props
        private const double molarMass = 18.01488;

        //Молярная масса воды
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния воды в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Water(bool _isSteam) : base(_isSteam)
        {

        }

        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            //double a0 = 0.0;
            //double a1 = 0.0;
            //double a2 = 0.0;
            //double a3 = 0.0;
            //double a4 = 0.0;
            //double a5 = 0.0;

            double density = 0.0;
            if (!this.isSteam) //Жидкость
            {
                //a0 = 1000.3916;
                //a1 = 0.068041205;
                //a2 = -0.0086770695;
                //a3 = 0.000070624106;
                //a4 = -0.00000045396011;
                //a5 = 1.2999754E-09;
                //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;

                //density = WspLib.wspDSWT(Math.Max(0, temperature) + 273.15);
                density = 1.0 / TechLib.VW(Math.Max(0, temperature));

            }
            else
            {
                //Плотность газа = P * 10^2/R/T(K)
                //R = 8.314
                //T(K) = t(Cels) + 273.15

                try
                {
                    //density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    //density = WspLib.wspDSST(temperature + 273.15);
                    //density = WspLib.wspDPT(pressure * 100000, temperature + 273.15);
                    //density = 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15);
                    density = Math.Max(0.0, 1.0 / TechLib.VS(pressure, temperature));

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
                a0 = 4.2149573;
                a1 = -0.0031526187;
                a2 = 0.00010044192;
                a3 = -1.526484e-006;
                a4 = 1.1975875e-008;
                a5 = -3.5978694e-011;

            }
            else
            {//Газ                
                a0 = 1.8557015;
                a1 = 0.0030295038;
                a2 = -0.00012286806;
                a3 = 0.0000021805877;
                a4 = -0.000000013160691;
                a5 = 2.8400593E-11;
            }

            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return - 1;
        }


        #endregion
    }
}
