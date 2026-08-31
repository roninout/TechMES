using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class Methan : Substance
    {
        #region fields & props
        private const double molarMass = 0.0160425; // kg/mol

        //Молярная масса
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Methan(bool _isSteam) : base(_isSteam)
        {

        }
        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            double r = 8.314462618;
            double z = 0.0;
            double density = 0.0;

            double x = (pressure - 3.05e6) / 2.95e6;        // normalized pressure
            double y = (temperature - 298.15) / 25.0;       // normalized temperature

            double y2 = y * y;
            double y3 = y2 * y;

            double x2 = x * x;
            double x3 = x2 * x;
            double x4 = x3 * x;

            z = (0.9358613118902501
                + 0.018918165383247483 * y
                - 0.0032063617804402264 * y2
                + 0.00043677853968546534 * y3)

                + x * (-0.05804905343899734
                + 0.01824068110096073 * y
                - 0.003409664414015895 * y2
                + 0.0005255698704910288 * y3)

                + x2 * (0.004355296859995484
                - 0.0003333147540060496 * y
                - 0.00023875322442202978 * y2
                + 9.647149260019897e-05 * y3)

                + x3 * (0.0004996240410321649
                - 0.0003140087813239308 * y
                + 9.080114116994517e-05 * y2
                - 1.445865457607404e-05 * y3)

                + x4 * (1.5293429759553303e-05
                - 4.358153213177035e-05 * y
                + 3.2705580661003505e-05 * y2
                - 1.1292031709083314e-05 * y3);

            density = this.MolarMass * pressure / r / temperature / z;

            //if (!this.isSteam) //Жидкость
            //{
            //    //a0 = 1000.3916;
            //    //a1 = 0.068041205;
            //    //a2 = -0.0086770695;
            //    //a3 = 0.000070624106;
            //    //a4 = -0.00000045396011;
            //    //a5 = 1.2999754E-09;
            //    //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;

            //    //density = WspLib.wspDSWT(Math.Max(0, temperature) + 273.15);
            //    density = 1.0 / TechLib.VW(Math.Max(0, temperature) + 273.15);

            //}
            //else
            //{
            //    //Плотность газа = P * 10^2/R/T(K)
            //    //R = 8.314
            //    //T(K) = t(Cels) + 273.15

            //    try
            //    {
            //        //density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
            //        //density = WspLib.wspDSST(temperature + 273.15);
            //        //density = WspLib.wspDPT(pressure * 100000, temperature + 273.15);
            //        //density = 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15);
            //        density = Math.Max(0.0, 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15));

            //    }
            //    catch (ArithmeticException)
            //    {

            //    }
            //}

            return density;
        }

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
            double a = 0.0;
            double b = 0.0;
            double c = 0.0;
            double d = 0.0;
            double e = 0.0;
            
            double capacity = 0.0;

            double temp = temperature / 1000.0;

            if (temperature < 1300.0)
            { //  298 to 1300	             
                a = -0.703029;
                b = 108.4773;
                c = -42.52157;
                d = 5.862788;
                e = 0.678565;

            }
            else
            {//  1300 to 6000              
                a = 85.81217;
                b = 11.26467;
                c = -2.114146;
                d = 0.138190;
                e = -26.42221;
            }

            capacity = (a + b * temp + c * Math.Pow(temp, 2) + d * Math.Pow(temp, 3) + e / Math.Pow(temp, 2)) / this.MolarMass;
            return capacity;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1;
        }

        #endregion
    }
}
