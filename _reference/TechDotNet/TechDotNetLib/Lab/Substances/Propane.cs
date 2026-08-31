using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    class Propane : Substance
    {
        #region fields & props

        private const double molarMass = 44.0956;       

        //Молярная масса Propane
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния Propane в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Propane(bool _isSteam) : base(_isSteam)
        {
        }

        #region Methods

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

                //Reciprocal Quadratic: y = 1 / (a + bx + cx ^ 2)

                //3rd degree Polynomial Fit:  y = a + bx + cx ^ 2 + dx ^ 3...	
                //Coefficient Data:	
                //a = 2.4507357
                //b = 0.007114219
                //c = 6.59E-05
                //d = 4.84E-07

                a0 = 2.4507357;
                a1 = 0.007114219;
                a2 = 6.59E-05;
                a3 = 4.84E-07;



                //capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                //capacity = 1 / (a0 + a1 * temperature + a2 * Math.Pow(temperature, 2));
                capacity = a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0; 
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
            double a4 = 0.0;
            double a5 = 0.0;

            double density = 0.0;

            if (!this.isSteam)
            { //Жидкость
              //y = a/b^(1 + (1 - t/c)^d)
                a0 = 1.3186;
                a1 = 0.27005;
                a2 = 369.86;
                a3 = 0.27852;

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
