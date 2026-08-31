using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
{
    class Freezium : Substance
    {
        #region fields & props

        private const double molarMass = -1;

        //Молярная масса Фризиума
        public override double MolarMass => molarMass;

        //Признак агрегатного состояния Фризиума в точке измерения
        public override bool IsSteam => isSteam;

        #endregion
        public Freezium(bool _isSteam = false) : base(_isSteam)
        {

        }
        #region methods


        //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК     
        public override double GetCapacity(float temperature)
        {
            //Считается теплоемкость исходя из разведенного фризиума до температуры -35 гр.С по таблице
            //   t     c
            //- 10    2.87
            //- 15    2.86
            //- 30    2.81
            //- 35    2.79
            //- 40    2.78
            //http://stron.com.ua/frizium-formiat-kaliya/freezium
            //\\192.168.1.3\Project\DOC_ACAD\Ukraine\КНХ\HPPO\Расчеты HPPO.xlsx

            double a0 = 0.0;
            double a1 = 0.0;
            double a2 = 0.0;
            double a3 = 0.0;
            double a4 = 0.0;
            double a5 = 0.0;

            double capacity = 0.0;

            if (!this.isSteam)
            { //Жидкость
                //y = a2*x^2 + a1*x + a0
                a0 = 2.8601143;
                a1 = -0.0033390476;
                a2 = -0.00027257143;
                a3 = -3.4666667e-006;
                a4 = 0.0;
                a5 = 0.0;


            }
            else
            {//Газ

                return -1.0; //Без расчета для газа
            }
            capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            return capacity;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public override double GetContent(float temperature, float pressure)
        {
            return -1.0;
        }

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

            if (!this.isSteam) //Жидкость
            {
                a0 = 1.028116;
                a1 = -0.0085052149;
                a2 = -0.00005551797;

                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
            }
            else //Газ
            {
                return -1.0; //Без расчета газа
            }

            return density;
        }

        #endregion
    }
}
