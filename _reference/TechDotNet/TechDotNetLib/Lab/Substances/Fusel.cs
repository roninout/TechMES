using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    internal class Fusel : Substance
    {
        #region fields & props
        private const double molarMass = 0.0160425; // kg/mol

        //Молярная масса
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния в точке измерения
        public override bool IsSteam => isSteam;

        #endregion

        public Fusel(bool _isSteam) : base(_isSteam)
        {

        }
        #region methods

        //Метод для определения плотности вещества при 100% концентрации, кг/м3
        public override double GetDensity(float temperature, float pressure)
        {
            double density = 0.0;
            //double rhoRef = 829.0;      // кг/м3 при 20°C для одного зразка fusel oil (приклад!)
            //double Tref_K = 293.15;     // 20°C
            //double pref_Pa = 101325.0;  // 1 атм
            //double alpha = 0.00095;     // 1/K (типова оцінка для органічних рідин)
            //double bulkModulus_Pa = 1.2e9; // Pa (порядок величини)

            //// Temperature correction (dominant)
            //double rhoT = rhoRef / (1.0 + alpha * (temperature - Tref_K));

            //// Pressure correction (small up to ~60 bar)
            //density = rhoT * (1.0 + (pressure - pref_Pa) / bulkModulus_Pa);
            const double Tref = 293.15;        // 20°C
            const double pref = 101325.0;      // 1 atm

            const double rhoRef = 975.0;       // kg/m3 (середина 0.970–0.980 g/ml із TDS)
            const double beta = 0.0006;      // 1/K (стартове для водно-спиртової емульсії)
            const double K = 2.0e9;       // Pa (порядок для рідин; тиск дає малий ефект)

            double rhoT = rhoRef / (1.0 + beta * (temperature - Tref));
            density = rhoT * (1.0 + (pressure - pref) / K);

            return density;
        }

        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
        public override double GetCapacity(float temperature)
        {
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
