using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace TechDotNetLib.Lab.Substances.WaterSteemProLib
{
    //Функции из библиотеки Water Steem Pro. Подключаются динамически из библиотеки okawsp6.dll
    //Нумерация функций взята из списка функций WaterSteemPro
    //Список функций WaterStemPro: http://www.wsp.ru/ru/documentation/wsp/6.0/funclist.htm

    public static class WspLib
    {
        #region Import Dll Functions
        //102. PSAT        
        /// <summary>
        /// Давление на линии насыщения[Па] как функция величин: температура t[K]:
        /// </summary>
        /// <param name="t">температура [K], тип: double</param>
        /// <returns>давление на линии насыщения[Па], тип: double</returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspPST(double t);


        //104. TSAT
        /// <summary>
        /// Температура на линии насыщения [K] как функция величин: давление p [Па]
        /// </summary>
        /// <param name="p">давление [Па], тип: double</param>
        /// <returns>температура на линии насыщения [K], тип: double</returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspTSP(double p);

        /// <summary>
        /// Плотность воды на линии насыщения
        /// </summary>
        /// <param name="t"></param>
        /// <returns>Плотность воды на линии насыщения</returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspDSWT(double t);


        /// <summary>
        /// Плотность пара на линии насыщения
        /// </summary>
        /// <param name="t"></param>
        /// <returns>Плотность пара на линии насыщения</returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspDSST(double t);


        /// <summary>
        ///  Плотность пара как функция от температуры и давления
        /// </summary>
        /// <param name="p"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspDPT(double p, double t);

        #endregion


        /// <summary>
        /// PSAT Давление на линии насыщения как функция величин: температура 
        /// </summary>
        /// <param name="t">Температура, гр. С</param>
        /// <returns>Давление, Bar(abs)</returns>
        public static double Psat(double t)
        {            
            return wspPST(t + 273.15) * 0.00001;
        }

        /// <summary>
        /// TSAT Температура на линии насыщения как функция величин: давление
        /// </summary>
        /// <param name="p">Давление, Bar(abs) </param>
        /// <returns>Температура, гр. С </returns>
        public static double Tsat(double p)
        {            
            return wspTSP(p * 100_000) - 273.15;
        }

        /// <summary>
        ///  Удельный объем м³/кг
        /// </summary>
        /// <param name="p"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspVPT(double p, double t);

        /// <summary>
        ///  Удельный объем пара на линии насыщения м³/кг
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        [DllImport("okawsp6.dll")]
        public static extern double wspVSST(double t);
    }
}
