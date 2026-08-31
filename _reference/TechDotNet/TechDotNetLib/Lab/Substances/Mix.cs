using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TechDotNetLib.Lab.Substances.WaterSteemProLib;
using TechDotNetLib.Lab.Substances.ContentCalculation;

namespace TechDotNetLib.Lab.Substances
{
    //Класс для хранения смеси веществ
    public class Mix
    {
        //Коллекция, в которой хранится состав компонентов
        private Dictionary<Substance, double> mixContent;
        private string[] components;    //Список компонентов
        private double[] contents;      //Величины содержаний компонентов в смеси, %

        //Свойство для обращения к словарю с компонентами из внешнего кода
        public Dictionary<Substance, double> MixContent
        {
            get
            {
                if (mixContent != null)
                    return mixContent;
                else
                    return new Dictionary<Substance, double>();
            }
        }
        public Mix()
        {
            mixContent = new Dictionary<Substance, double>();
        }


        public Mix(string[] _components, double[] _contents) : this()
        {
            components = _components;
            contents = _contents;

            this.InitalizeSubstance(components, contents);
        }

        public Mix(string[] _components) : this()
        {
            components = _components;
            contents = new double[_components.Length];
            contents.Select(c => { c = 0; return c; });

            this.InitalizeSubstance(components, contents);
        }


        private void AddComponent(Substance substance, double content)
        {
            if (mixContent != null)
            {
                mixContent.Add(substance, content);
            }

        }

        //Метод инициализации словаря компон
        private void InitalizeSubstance(string[] _components, double[] _contents)
        {
            Substance sub = null;
            for (int i = 0; i < _components.Length; i++)
            {
                switch (_components[i])
                {
                    //Алкоголь
                    case "ALC":
                        sub = new Alcohol(false);      // рідина
                        break;
                    case "ALCS":
                        sub = new Alcohol(true);       // газ
                        break;

                    //Ацетонитрил
                    case "ACA":
                        sub = new Acetaldehyde(false);      //жидкость
                        break;
                    case "ACAS":
                        sub = new Acetaldehyde(true);       //газ
                        break;

                    //Ацетонитрил
                    case "ACN":
                        sub = new Acetonitrile(false);      //жидкость
                        break;
                    case "ACNS":
                        sub = new Acetonitrile(true);       //газ
                        break;

                    //Перекись водорода
                    case "HP":
                        sub = new HydrohenPeroxyde(false);  //жидкость
                        break;
                    case "HPS":
                        sub = new HydrohenPeroxyde(true);   //газ
                        break;

                    //Азот
                    case "N":
                        sub = new Nitrogen();               //газ
                        break;

                    //Кислород
                    case "O2":
                        sub = new Oxygen();                 //газ
                        break;

                    //Пропилен
                    case "P":
                        sub = new Propylene(false);         //жидкость
                        break;
                    case "PS":
                        sub = new Propylene(true);          //газ
                        break;

                    //ПропиленОксид
                    case "PO":
                        sub = new PropyleneOxyde(false);    //жидкость
                        break;
                    case "POS":
                        sub = new PropyleneOxyde(true);     //газ
                        break;

                    //Вода
                    case "Water":
                        sub = new Water(false);             //жидкость
                        break;
                    case "WaterS":
                        sub = new Water(true);              //газ
                        break;

                    //1,2-Butadiene
                    case "Butadiene_1_2":
                        sub = new Butadiene_1_2(false);     //жидкость
                        break;
                    case "Butadiene_1_2S":
                        sub = new Butadiene_1_2(true);      //газ
                        break;

                    //1,3-Butadiene
                    case "Butadiene_1_3":
                        sub = new Butadiene_1_3(false);     //жидкость
                        break;
                    case "Butadiene_1_3S":
                        sub = new Butadiene_1_3(true);      //газ
                        break;

                    //1-Butene
                    case "Butene_1":
                        sub = new Butene_1(false);          //жидкость
                        break;
                    case "Butene_1S":
                        sub = new Butene_1(true);           //газ
                        break;

                    //Cis-2-Butene
                    case "Cis-2-Butene":
                        sub = new Cis_2_Butene(false);      //жидкость
                        break;
                    case "Cis-2-ButeneS":
                        sub = new Cis_2_Butene(true);       //газ
                        break;

                    //Ethane
                    case "Ethane":
                        sub = new Ethane(false);            //жидкость
                        break;
                    case "EthaneS":
                        sub = new Ethane(true);             //газ
                        break;

                    //Ethylene
                    case "Ethylene":
                        sub = new Ethylene(false);          //жидкость
                        break;
                    case "EthyleneS":
                        sub = new Ethylene(true);           //газ
                        break;

                    //Isobutane
                    case "Isobutane":
                        sub = new Isobutane(false);         //жидкость
                        break;
                    case "IsobutaneS":
                        sub = new Isobutane(true);          //газ
                        break;

                    //Methyl-Acetylene
                    case "Methyl-Acetylene":
                        sub = new Methyl_Acetylene(false);  //жидкость
                        break;
                    case "Methyl-AcetyleneS":
                        sub = new Methyl_Acetylene(true);   //газ
                        break;

                    //n-Butane
                    case "n-Butane":
                        sub = new N_Butane(false);         //жидкость
                        break;
                    case "n-ButaneS":
                        sub = new N_Butane(true);          //газ
                        break;

                    //n-Pentane
                    case "n-Pentane":
                        sub = new N_Pentane(false);       //жидкость
                        break;
                    case "n-PentaneS":
                        sub = new N_Pentane(true);        //газ
                        break;

                    //Propadiene
                    case "Propadiene":
                        sub = new Propadiene(false);     //жидкость
                        break;
                    case "PropadieneS":
                        sub = new Propadiene(true);      //газ
                        break;

                    //Propane
                    case "Pr":
                        sub = new Propane(false);       //жидкость
                        break;
                    case "PrS":
                        sub = new Propane(true);        //газ
                        break;

                    //Trans-2-Butene
                    case "Trans-2-Butene":
                        sub = new Trans_2_Butene(false);  //жидкость
                        break;
                    case "Trans-2-ButeneS":
                        sub = new Trans_2_Butene(true);   //газ
                        break;

                    //Vinylacetylene
                    case "Vinylacetylene":
                        sub = new Vinylacetylene(false);     //жидкость
                        break;
                    case "VinylacetyleneS":
                        sub = new Vinylacetylene(true);      //газ
                        break;
                    //Freezium
                    case "Freezium":
                        sub = new Freezium(false);     //жидкость
                        break;
                    //Ethanol
                    case "Ethanol":
                        sub = new Ethanol(false);     //жидкость
                        break;
                    //Addition
                    case "Addition":
                        sub = new Ethanol(false);     //жидкость
                        break;

                    //Diesel
                    case "Diesel":
                        sub = new Diesel(false);     // рідина
                        break;

                    //Sodium hydroxide
                    case "NaOH":
                        sub = new NaOH(false);  //жидкость
                        break;
                    case "NaOHS":
                        sub = new NaOH(true);   //газ
                        break;

                    //Hydrochloric acid
                    case "HCL":
                        sub = new HCL(false);  //жидкость
                        break;
                    case "HCLS":
                        sub = new HCL(true);   //газ
                        break;

                    //Methan
                    case "Methan":
                        sub = new Methan(false);     // рідина
                        break;

                    //Fusel
                    case "Fusel":
                        sub = new Fusel(false);     // рідина
                        break;

                    //Sug
                    case "Sug":
                        sub = new Sug(false);     // рідина
                        break;

                    default:
                        //Неизветсное вещество
                        sub = new UnknownSubstance();
                        break;
                }
                mixContent.Add(sub, _contents[i]);
            }
        }


        //Метод для расчета плотности смеси
        public double GetDensity(float _temp, float? _press)
        {
            if (mixContent != null)
            {
                double tmp_density = 0;
                foreach (KeyValuePair<Substance, double> pair in mixContent)
                {
                    if (pair.Key is UnknownSubstance)
                        return -1.0;

                    tmp_density += pair.Value * 0.01 / pair.Key.GetDensity(_temp, _press ?? 0);
                }
                return (1 / tmp_density) * 10.0;
            }
            else
                return 0.0;

        }

        //Метод для расчета теплоемкости смеси
        public double GetCapacity(float _temp, float? _press)
        {
            if (MixContent != null)
            {
                double tmp_capacity = 0; ;
                foreach (KeyValuePair<Substance, double> pair in mixContent)
                {
                    if (pair.Key is UnknownSubstance)
                        return -1.0;

                    tmp_capacity += pair.Value * 0.01 * pair.Key.GetCapacity(_temp);
                }
                return tmp_capacity * 1000.0;
            }
            else
                return 0.0;
        }

        //Метод для определения концентрации вещества в N-компонентной смеси
        public double GetContent(float _temp, float? _press, int numOfComponentToCalculate)
        {
            double tmp_content = 0;
            if (MixContent != null)
            {
                try
                {
                    tmp_content = mixContent.ElementAt(numOfComponentToCalculate).Key.GetContent(_temp, _press ?? 0);
                }
                catch (Exception)
                {
                }
            }

            return tmp_content;
        }

        public double[] GetContent(float _temp, float _press, int configurationCode)
        {

            //configurationCode = 10 : разряд единиц - признак снятия ограничения 0-100% (0 - не снято; 1 - снято); Разряд десятков - выбор формулы для расчетов

            //Инициализируем начальный массив содержаний по длине состава смеси
            double[] tmp_content = new double[mixContent.Count];

            for (int i = 0; i < tmp_content.Length; i++)
            {
                tmp_content[i] = -1.0;
            }
            Substance component_1 = mixContent.ElementAt(0).Key;
            Substance component_2 = mixContent.ElementAt(1).Key;
            Substance component_3 = mixContent.ElementAt(2).Key;


            Func<float, float, int, double[]> CalculateContentFunc = null;

            #region Calculation Content for different Pairs
            //Пара Алкоголь - Вода
            if (component_1 is Alcohol && component_2 is Water)
                CalculateContentFunc = ((t, p, c) => ContentCalc.ALC_Water_Content(t, p, c));

            //Пара Ацетонитрил - Вода
            if (component_1 is Acetonitrile && component_2 is Water)            
                CalculateContentFunc = ((t, p, c) => ContentCalc.ACN_Water_Content(t, p, c));

            //Пара Вода -Ацетонитрил
            if (component_1 is Water && component_2 is Acetonitrile)
                CalculateContentFunc = ((t, p, c) => ContentCalc.Water_ACN_Content(t, p, c));


            //Пара ПропиленОксид - Пропилен
            if (component_1 is PropyleneOxyde && component_2 is Propylene)            
                CalculateContentFunc = ((t, p, c) => ContentCalc.PO_P_Content(t, p, c));

            //Пара Пропилен - ПропиленОксид
            if (component_1 is Propylene && component_2 is PropyleneOxyde)
                CalculateContentFunc = ((t, p, c) => ContentCalc.P_PO_Content(t, p, c));

            //Трио Ацетонитрил - Вода - Пропилен Оксид
            if (component_1 is Acetonitrile && component_2 is Water && component_3 is PropyleneOxyde)
                CalculateContentFunc = ((t, p, c) => ContentCalc.ACN_Water_PO_Content(t, p, c));           
               

            //Трио Пропилен Оксид - Вода - Ацетонитрил
            if (component_1 is PropyleneOxyde && component_2 is Water && component_3 is Acetonitrile)            
                CalculateContentFunc = ((t, p, c) => ContentCalc.PO_Water_ACN_Content(t, p, c));

            //Пара Пропилен-оксид - Вода
            if (component_1 is PropyleneOxyde && component_2 is Water)
                CalculateContentFunc = ((t, p, c) => ContentCalc.PO_Water_Content(t, p, c));

            //Пара Вода - Пропилен-оксид
            if (component_1 is Water && component_2 is PropyleneOxyde)
                CalculateContentFunc = ((t, p, c) => ContentCalc.Water_PO_Content(t, p, c));
                        
            //Пара Ацетальдегид - Пропилен-оксид
            if (component_1 is Acetaldehyde && component_2 is PropyleneOxyde)
                CalculateContentFunc = ((t, p, c) => ContentCalc.ACA_PO_Content(t, p, c));

            //Пара Пропилен-оксид - Ацетальдегид
            if (component_1 is PropyleneOxyde && component_2 is Acetaldehyde)
                CalculateContentFunc = ((t, p, c) => ContentCalc.PO_ACA_Content(t, p, c));

            #endregion


            //Вызываем метод для выбранной пары компонентов
            if (CalculateContentFunc != null)
                Array.Copy(CalculateContentFunc.Invoke(_temp, _press, configurationCode), 0, tmp_content, 0, 3);

            return tmp_content;
        }
        

    }


}
