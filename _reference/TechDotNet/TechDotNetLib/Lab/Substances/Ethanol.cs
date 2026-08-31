using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class Ethanol : Substance
    {
        #region fields & props

        private const double molarMass = 46.06804;

        //Молярная масса этанола
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния этанола в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Ethanol(bool _isSteam = false) : base(_isSteam) //Этанол - всегда жидкость!!!
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

            if (temperature < 78.2)
            {
                a0 = 806.08;
                a1 = -0.8158;
                a2 = -0.0002567;
                a3 = -0.000008873;                

            }
            else
            {
                a0 = 775.2;
                a1 = 0.2803;
                a2 = -0.01468;
                a3 = 0.00007474;
                a4 = -0.0000001793;

            }
            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
            density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
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

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1;
        }

        #endregion


    }
}
