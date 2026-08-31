using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class Sug : Substance
    {

        #region fields & props

        private const double molarMass = 41.0524;

        //Молярная масса ацетонитрила
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния ацетонитрила в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Sug(bool _isSteam) : base(_isSteam)
        {

        }

        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            double a = 998.2;
            double b = 3.85;
            double c = 0.015;   // концентрация в % СВ
            double d = 0.12;
            double e = 0.008;

            double density = 0.0;

            density = (a + b * c + c * Math.Pow(c, 2)) - (d + e * c) * (temperature - 20);

            return density;
        }

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
            double capacity = 0.0;

            return -1;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1;
        }

        #endregion

    }
}
