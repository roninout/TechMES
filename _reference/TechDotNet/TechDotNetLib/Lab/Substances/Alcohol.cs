using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TechDotNetLib.Lab.Substances.WaterSteemProLib;

namespace TechDotNetLib.Lab.Substances
{
    internal class Alcohol : Substance
    {
        
        #region fields & props

        private const double molarMass = 41.0524;        

        //Молярная масса ацетонитрила
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния ацетонитрила в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Alcohol(bool _isSteam) : base(_isSteam)
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

            if (!this.isSteam) // ---- Liquid ----
            {
                //a0 = 803.07;
                //a1 = -1.0542;

                ////y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                a0 = 806.35287;
                a1 = -0.85573105;
                a2 = 0.000306297;
                a3 = -7.26E-06;
                a4 = -2.02E-08;

                density = a0 + a1 * temperature + a2 * Math.Pow(temperature, 2) + a3 * Math.Pow(temperature, 3) + a4 * Math.Pow(temperature, 4);
            }
            else // ---- Vapor: ideal gas ----
            {
                //Плотность газа = P * 10^2/R/T(K)
                //R = 8.314
                //T(K) = t(Cels) + 273.15

                try // ---- Vapor: Peng–Robinson EOS ----
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

            if (!this.isSteam) // ---- Liquid ----
            { 
                //y = a2*x^2 + a1*x + a0
                //a0 = 2.1864307;
                //a1 = 0.0015649999;
                //a2 = 0.0000083021163;

                //y = a0 + a1 * x + a2 * x^2
                a0 = 2.2891429;
                a1 = 0.0095564286;
                a2 = 0.000026964286;

                capacity = a0 + a1 * temperature + a2 * Math.Pow(temperature, 2);
                return capacity;
            }
            else // ---- Vapor: ideal gas ----
            {

                a0 = 1.2125728;
                a1 = 0.0022147106;
                a2 = 0.0000024869344;
                a3 = -0.000000025107206;
                a4 = 5.9195896E-11;
                a5 = 0.0;

                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }

            //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            //return capacity;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            double content;
            double alcMass;
            double a0 = -0.071728663;
            double a1 = 1.2743981;
            double a2 = 0.001897273;
            double a3 = 8.29E-06; //0.00000829;

            // Масовий вміст алкоголю
            //alcMass = (temperature - WspLib.Tsat((float)pressure)) * 100.0 / (1670.409 / (5.37229 - Math.Log((float)(pressure) * 0.98717) * 0.434294) - 232.959 - WspLib.Tsat((float)pressure));
            alcMass = (temperature - TechLib.TSAT((float)pressure)) * 100.0 / (1670.409 / (5.37229 - Math.Log((float)(pressure) * 0.98717) * 0.434294) - 232.959 - TechLib.TSAT((float)pressure));

            // Обмеження 0.0 - 100.0
            alcMass = Math.Max(0, Math.Min(100.0, alcMass));

            content = a0 + a1 * alcMass - a2 * Math.Pow(alcMass, 2) - a3 * Math.Pow(alcMass, 3);
            return content; 
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

        

        #endregion

    }
}
