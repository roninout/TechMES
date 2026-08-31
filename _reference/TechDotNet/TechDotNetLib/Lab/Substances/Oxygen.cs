using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class Oxygen : Substance
    {
        #region fields & props

        private const double molarMass = 31.998;

        //Молярная масса кислорода
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния перекиси водорода в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Oxygen(bool _isSteam = true) : base(_isSteam) //Кислород - всегда газ!!!
        {

        }

        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            //Плотность газа = P * 10^2/R/T(K)
            //R = 8.314
            //T(K) = t(Cels) + 273.15

            double density = 0.0;
            try
            {
                density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
            }
            catch (ArithmeticException)
            {

            }
            return density;
        }

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
            double capacity = 0.0;

            double a0 = 0.91382605;
            double a1 = 0.00010521232;
            double a2 = 0.0000007952104;
            double a3 = 9.5228327E-10;
            double a4 = -9.0575044E-12;
            double a5 = 0.0;

            //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;

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
