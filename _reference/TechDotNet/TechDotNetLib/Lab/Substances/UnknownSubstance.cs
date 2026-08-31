using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class UnknownSubstance : Substance
    {
        #region fields & props

        //Молярная масса
        public override double MolarMass => -1.0;

        //Признак агрегатного состояния вещества
        public override bool IsSteam => false;

        #endregion

        public UnknownSubstance(bool _isSteam = false) : base(_isSteam)
        {

        }

        #region methods

        public override double GetCapacity(float temperature)
        {
            return -1.0;
        }

        public override double GetDensity(float temperature, float pressure)
        {
            return -1.0;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1.0;
        }

        #endregion
    }
}
