using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    class Acetaldehyde : Substance
    {
        #region fields & props

        private const double molarMass = 44.0;

        //Молярная масса ацетонитрила
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния ацетонитрила в точке измерения
        public override bool IsSteam => isSteam;

        #endregion
        public Acetaldehyde(bool _isSteam) : base(_isSteam)
        {

        }

        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {            

            return 0.0;
        }

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {            
            return 0.0;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1.0;
        }      



        #endregion
    }
}
