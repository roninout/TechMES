using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TechDotNetLib.Lab.Substances.WaterSteemProLib;

namespace TechDotNetLib.Lab.Substances
{
    internal class HCL : Substance
    {

        #region fields & props

        private const double molarMass = 36.46000;

        // Молярна маса diesel
        public override double MolarMass => molarMass;

        // Ознака агрегатного стану diesel у точці вимірювання
        public override bool IsSteam => isSteam;

        #endregion

        public HCL(bool _isSteam = false) : base(_isSteam) // HCL - завжди рідина!!!
        {

        }

        #region methods

        // Метод для визначення густини речовини при 100% концентрації, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            //double a0 = 0.0;
            //double a1 = 0.0;
            //double a2 = 0.0;
            //double a3 = 0.0;
            //double a4 = 0.0;
            //double a5 = 0.0;

            //double density = 0.0;

            //if (temperature < 78.2)
            //{
            //    a0 = 806.08;
            //    a1 = -0.8158;
            //    a2 = -0.0002567;
            //    a3 = -0.000008873;

            //}
            //else
            //{
            //    a0 = 775.2;
            //    a1 = 0.2803;
            //    a2 = -0.01468;
            //    a3 = 0.00007474;
            //    a4 = -0.0000001793;

            //}
            ////y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
            //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;

            //double q15 = 860.0; // kg/m3
            double q20 = 856.5; // kg/m3
            double c0 = 0.7;    // kg/m3

            double density = 0.0;

            density = q20 - (temperature - 20.0) * c0;
            return density;
        }

        // Метод для визначення теплоємності речовини при 100% концентрації, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
            double a0 = 0.0;
            double a1 = 0.0;
            double a2 = 0.0;
            double a3 = 0.0;
            double a4 = 0.0;
            double a5 = 0.0;

            double capacity = 0.0;

            if (temperature < 78.2)
            {
                a0 = 2268.83;
                a1 = 11.78;
                a2 = 0.03051;
                a3 = -0.0006118;
                a4 = 0.000002707;

            }
            else
            {
                a0 = 3774.52;
                a1 = -39.65;
                a2 = 0.6675;
                a3 = -0.003946;
                a4 = 0.000008637;

            }
            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
            capacity = (a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0) * 0.001;
            return capacity;
        }

        // Метод для визначення концентрації речовини в N-компонентній суміші
        public override double GetContent(float temperature, float pressure)
        {
            return -1;
        }

        #endregion


    }
}
