using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    class Butadiene_1_3 : Substance
    {
        #region fields & props

        private const double molarMass = 54.0904;

        //Молярная масса бутадиена 1 3
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния бутадиена 1 3 в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Butadiene_1_3(bool _isSteam) : base(_isSteam)
        {
        }

        #region methods
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
            {   //Жидкость
                //y = a0 + exp b/t + c + dt + et^2
                a0 = 88166;
                a1 = 583.44;
                a2 = 1.8231;
                a3 = 0.030118;
                a4 = -0.000025695;
                a5 = 0;
                capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
            }
            else
            {//Газ

                //a0 = 0.86492;
                //a1 = 0.22148;
                //a2 = 452;
                //a3 = 0.28373;
                //a4 = 1.7356035E-10;
                //a5 = -3.0549926E-13;
                capacity = 0.0;

            }
            
            //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        public override double GetContent(float temperature, float pressure)
        {
            throw new NotImplementedException();
        }

        public override double GetDensity(float temperature, float pressure)
        {
            double a0 = 0.0;
            double a1 = 0.0;
            double a2 = 0.0;
            double a3 = 0.0;            

            double density = 0.0;

            if (!this.isSteam)
            {//Жидкость
             //y = a/b^(1 + (1 - t/c)^d)                
                a0 = 1.3314;
                a1 = 0.28213;
                a2 = 425;
                a3 = 0.30137;

                density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;

            }
            else
            {//Газ

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
        #endregion
    }
}
