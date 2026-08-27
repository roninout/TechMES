using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechMES.Calc.Thermodynamics
{
    /// <summary>
    /// Перенесені з розрахунки з PLC.
    /// </summary>
    internal class TechLib
    {
        private static readonly double[,] A = new double[4, 6]
        {
            {  0.8853863,   -4.42932,    9.961583,  -13.68941,   7.604213,   -1.797136 },
            { 24.24687,   -156.3135,   399.1567,  -503.6067,  313.5063,   -77.09810 },
            { -1.997394,   -4.75184,    63.77667, -156.2124,  152.9724,   -53.96937 },
            {  0.7527841,  -1.94789,    -2.32442,   11.62933, -12.01737,    3.84319 }
        };

        /// <summary>
        /// VS – питомий об'єм водяної пари, м³/кг
        /// p - тиск в bar(abs)
        /// t - температура в °C
        /// </summary>
        public static double VS(double p, double t)
        {
            double theta = (t + 273.15) / 647.3;
            // В оригинале рядом оставался закомментированный вариант +100000 Pa.
            // Рабочая формула TechDotNetLib использовала только p * 100000.
            double beta = (p * 100000.0) / 22.12e6;

            double chi = 4.2603 * theta / beta;
            double ps = 1.0;
            double xx = 0.0;

            for (int i = 0; i < 4; i++)
            {
                xx += 1.0;
                double s = 0.0;
                double ts = 1.0;

                for (int j = 0; j <= 5; j++)
                {
                    s += A[i, j] / ts;
                    ts *= theta;
                }

                chi += s * xx * ps;
                ps *= beta;
            }

            // CORR-блок
            double b8 = Math.Pow(beta, 8.0);
            double b12 = Math.Pow(beta, 12.0);
            double d2b8 = 1.755e-2 + b8;
            double d4b12 = 1.24828e-2 + b12;

            double xx1 = Math.Pow(theta, 5.0);
            double xx2 = Math.Pow((beta / xx1), 7.0);

            double xx3 = Math.Pow(theta, 7.0);
            double xx4 = Math.Pow((beta / xx3), 10.0);

            chi += 8.0 * 1.7988e-3 / d2b8 * xx2 * (1.0 - b8 / d2b8)
                 + 12.0 * (-4.06007e-4) / d4b12 * xx4 * beta / theta * (1.0 - b12 / d4b12);

            return chi * 3.17e-3; // м³/кг
        }

        /// <summary>
        /// VW – питомий об'єм рідини (води), м³/кг
        /// t - температура в °C
        /// </summary>
        public static double VW(double t)
        {
            //t -= 273.15;
            double temp = 1000.1353
                          + 0.00076933504 * t
                          - 0.0056218464 * Math.Pow(t, 2.0)
                          + 1.7341396e-5 * Math.Pow(t, 3.0)
                          - 3.089613e-8 * Math.Pow(t, 4.0);

            if (temp != 0.0)
                return 1.0 / temp;
            return 0.0001;
        }

        /// <summary>
        /// VSS – эффективный удельный объём сухих веществ сахарного раствора, м³/кг.
        ///
        /// Исходная PLC-корреляция определяла плотность готового сахарного раствора:
        ///
        ///     rhoSolution = rhoWater
        ///                 + s   * A(T)
        ///                 + s^2 * B(T)
        ///                 + s^3 * C(T)
        ///
        /// где:
        ///     s = массовая доля сухих веществ 0..1.
        ///
        /// В TechMES вода является отдельным компонентом смеси.
        /// Поэтому VSS не возвращает удельный объём всего раствора.
        /// Из исходной ICUMSA-корреляции алгебраически исключается вклад Water,
        /// и возвращается такой эффективный удельный объём DryMatter,
        /// при котором стандартная формула смеси:
        ///
        ///     1 / rhoMix = wWater / rhoWater + wDryMatter / rhoDryMatter
        ///
        /// даёт точно ту же плотность, что исходный PLC-расчёт.
        ///
        /// temperature      - температура, °C.
        /// dryMatterPercent - массовая концентрация сухих веществ, %.
        /// </summary>
        public static double VSS(double temperature, double dryMatterPercent)
        {
            if (!double.IsFinite(temperature))
                throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be a finite number.");

            if (!double.IsFinite(dryMatterPercent) || dryMatterPercent <= 0.0 || dryMatterPercent > 100.0)
                throw new ArgumentOutOfRangeException(nameof(dryMatterPercent), "Dry matter percent must be greater than 0 and not greater than 100.");

            // Исходная формула рассчитана для температуры от 0 °C.
            // Как и в Water.GetDensity(), отрицательную температуру ограничиваем нулём.
            var t = Math.Max(0.0, temperature);
            var t2 = t * t;

            // Массовая доля сухих веществ 0..1.
            var s = dryMatterPercent * 0.01;

            // Плотность Water не дублируем отдельной формулой.
            // Используем тот же VW(), который использует компонент Water.
            var waterSpecificVolume = VW(t);

            if (!double.IsFinite(waterSpecificVolume) || waterSpecificVolume <= 0.0)
                throw new ArithmeticException("Calculated Water specific volume is invalid.");

            var waterDensity = 1.0 / waterSpecificVolume;

            // Исходный вклад сухих веществ из PLC-корреляции ICUMSA.
            //
            // Эта часть перенесена буквально:
            //
            // DS * 0.01 * (385.1761 - 0.1343*T - 0.0031*T^2)
            // + (DS * 0.01)^2 * (154.316 - 0.4357*T + 0.0016*T^2)
            // + (DS * 0.01)^3 * (71.52 + 0.842*T - 0.0055*T^2)
            var dryMatterDensityContribution =
                s * (385.1761 - 0.1343 * t - 0.0031 * t2)
                + s * s * (154.316 - 0.4357 * t + 0.0016 * t2)
                + s * s * s * (71.52 + 0.842 * t - 0.0055 * t2);

            // Плотность всего сахарного раствора по исходной PLC-формуле.
            var solutionDensity = waterDensity + dryMatterDensityContribution;

            if (!double.IsFinite(solutionDensity) || solutionDensity <= 0.0)
                throw new ArithmeticException("Calculated sugar solution density is invalid.");

            var solutionSpecificVolume = 1.0 / solutionDensity;
            var waterMassFraction = 1.0 - s;

            // Стандартная формула нашего MixturePropertyCalculator:
            //
            // Vsolution = wWater * Vwater + wDryMatter * VdryMatter
            //
            // Отсюда:
            //
            // VdryMatter =
            //     (Vsolution - wWater * Vwater) / wDryMatter
            //
            // Таким образом вклад Water из исходной формулы исключается,
            // потому что Water будет добавлен отдельно самим MixturePropertyCalculator.
            var dryMatterSpecificVolume =
                (solutionSpecificVolume - waterMassFraction * waterSpecificVolume) / s;

            if (!double.IsFinite(dryMatterSpecificVolume) || dryMatterSpecificVolume <= 0.0)
                throw new ArithmeticException("Calculated DryMatter specific volume is invalid.");

            return dryMatterSpecificVolume;
        }

        /// <summary>
        /// CSS – удельная теплоёмкость сахарного раствора, J/(kg·K).
        ///
        /// Формула перенесена из PLC без изменения коэффициентов.
        ///
        /// temperature      - температура раствора, °C;
        /// dryMatterPercent - массовая концентрация сухих веществ DS, %;
        /// purityPercent    - чистота сухих веществ PUR, %.
        ///
        /// При T > 0 °C используется формула НУХТ с Log10.
        /// При T <= 0 °C используется исходная эмпирическая формула.
        /// </summary>
        public static double CSS(double temperature, double dryMatterPercent, double purityPercent)
        {
            if (!double.IsFinite(temperature))
                throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be a finite number.");

            if (!double.IsFinite(dryMatterPercent) || dryMatterPercent < 0.0 || dryMatterPercent > 100.0)
                throw new ArgumentOutOfRangeException(nameof(dryMatterPercent), "Dry matter percent must be between 0 and 100.");

            if (!double.IsFinite(purityPercent) || purityPercent < 0.0 || purityPercent > 100.0)
                throw new ArgumentOutOfRangeException(nameof(purityPercent), "Purity percent must be between 0 and 100.");

            if (temperature > 0.0)
                return 4218.0 + 2.8 * temperature * Math.Log10(0.01 * temperature) - dryMatterPercent * (29.73 - 0.07536 * temperature - 0.046 * purityPercent);

            return 4186.8 * (1.0 - (0.6 - 0.0018 * temperature) * dryMatterPercent * 0.01);
        }

        /// <summary>
        /// VG – питомий об'єм метану, м³/кг
        /// </summary>
        public static double VG(double p, double t)
        {
            // В оригинале +1.0 было закомментировано и в расчёте не участвовало.
            double temp = 193.1718 * p / (t + 273.15);
            if (temp != 0.0)
                return 1.0 / temp;
            return 0.7169;
        }

        /// <summary>
        /// VA – питомий об'єм повітря, м³/кг
        /// p - тиск в bar(abs)
        /// t - температура в °C
        /// </summary>
        public static double VA(double p, double t)
        {
            // В оригинале +1.0 было закомментировано и в расчёте не участвовало.
            double temp = 352.65 * (p * 0.986923) / (t + 273.15);
            if (temp != 0.0)
                return 1.0 / temp;
            return 0.8163;
        }

        /// <summary>
        /// HCL_DENS – густина водного розчину HCl, т/м³.
        /// temperature - температура в °C
        /// Повертає false, якщо концентрація або температура поза табличним діапазоном.
        /// </summary>
        public static double HclDensity(double concentration, double temperature, out bool countOk)
        {
            double concTemp;
            double dens;

            // 1. Початково вважаємо, що все в межах
            countOk = true;

            // 2. Межі концентрації 0..60 %
            if (concentration < 0.0)
            {
                concTemp = 0.0;
                countOk = false;
            }
            else if (concentration > 60.0)
            {
                concTemp = 60.0;
                countOk = false;
            }
            else
            {
                concTemp = concentration;
            }

            // 3. Межі температури -5..100 °C – поза ними просто екстраполяція з фіксованої кривої
            if (temperature < -5.0)
            {
                countOk = false;
                dens = 1.0005345 + 0.0051082126 * concTemp + 0.000014905384 * concTemp * concTemp;
                return dens;
            }
            if (temperature > 100.0)
            {
                countOk = false;
                dens = 0.95865571 + 0.0051357054 * concTemp - 0.000010172859 * concTemp * concTemp;
                return dens;
            }

            // 4. Усередині діапазону – інтерполяція по сусідніх ізотермах
            double densMax, densMin;
            double tMin, tMax;

            if (temperature >= -5.0 && temperature < 0.0)
            {
                tMin = -5.0; tMax = 0.0;
                densMax = 1.0005345 + 0.0051082126 * concTemp + 0.000014905384 * concTemp * concTemp;
                densMin = 1.0008363 + 0.005038634 * concTemp + 0.000012453666 * concTemp * concTemp;
            }
            else if (temperature >= 0.0 && temperature < 10.0)
            {
                tMin = 0.0; tMax = 10.0;
                densMax = 1.0008363 + 0.005038634 * concTemp + 0.000012453666 * concTemp * concTemp;
                densMin = 1.000023 + 0.0049718434 * concTemp + 0.000007546524 * concTemp * concTemp;
            }
            else if (temperature >= 10.0 && temperature < 20.0)
            {
                tMin = 10.0; tMax = 20.0;
                densMax = 1.000023 + 0.0049718434 * concTemp + 0.000007546524 * concTemp * concTemp;
                densMin = 0.99771759 + 0.0050194534 * concTemp + 0.00000032582654 * concTemp * concTemp;
            }
            else if (temperature >= 20.0 && temperature < 40.0)
            {
                tMin = 20.0; tMax = 40.0;
                densMax = 0.99771759 + 0.0050194534 * concTemp + 0.00000032582654 * concTemp * concTemp;
                densMin = 0.99195926 + 0.0048451435 * concTemp - 0.000000079440352 * concTemp * concTemp;
            }
            else if (temperature >= 40.0 && temperature < 60.0)
            {
                tMin = 40.0; tMax = 60.0;
                densMax = 0.99195926 + 0.0048451435 * concTemp - 0.000000079440352 * concTemp * concTemp;
                densMin = 0.98317993 + 0.0048273259 * concTemp - 0.0000019441027 * concTemp * concTemp;
            }
            else if (temperature >= 60.0 && temperature < 80.0)
            {
                tMin = 60.0; tMax = 80.0;
                densMax = 0.98317993 + 0.0048273259 * concTemp - 0.0000019441027 * concTemp * concTemp;
                densMin = 0.97218557 + 0.0048892524 * concTemp - 0.0000039182921 * concTemp * concTemp;
            }
            else // 80..100
            {
                tMin = 80.0; tMax = 100.0;
                densMax = 0.97218557 + 0.0048892524 * concTemp - 0.0000039182921 * concTemp * concTemp;
                densMin = 0.95865571 + 0.0051357054 * concTemp - 0.000010172859 * concTemp * concTemp;
            }

            dens = densMax - (densMax - densMin) *
                  ((temperature - tMin) / (tMax - tMin));

            return dens;
        }

        /// <summary>
        /// NAON_DENS – густина водного розчину NaOH (NaON), т/м³.
        /// temperature - температура в °C
        /// </summary>
        public static double NaOHDensity(double concentration, double temperature, out bool countOk)
        {
            double concTemp;
            double dens;

            countOk = true;

            // Межі концентрації 0..60 %
            if (concentration < 0.0)
            {
                concTemp = 0.0;
                countOk = false;
            }
            else if (concentration > 60.0)
            {
                concTemp = 60.0;
                countOk = false;
            }
            else
            {
                concTemp = concentration;
            }

            // Межі температури 0..100 °C
            if (temperature < 0.0)
            {
                countOk = false;
                dens = 0.99989452 + 0.012028427 * concTemp - 0.000024129689 * concTemp * concTemp;
                return dens;
            }
            if (temperature > 100.0)
            {
                countOk = false;
                dens = 0.95682375 + 0.011033307 * concTemp - 0.000015281934 * concTemp * concTemp;
                return dens;
            }

            double densMax, densMin;
            double tMin, tMax;

            if (temperature >= 0.0 && temperature < 15.0)
            {
                tMin = 0.0; tMax = 15.0;
                densMax = 0.99989452 + 0.012028427 * concTemp - 0.000024129689 * concTemp * concTemp;
                densMin = 0.99771554 + 0.0116776 * concTemp - 0.000020481759 * concTemp * concTemp;
            }
            else if (temperature >= 15.0 && temperature < 20.0)
            {
                tMin = 15.0; tMax = 20.0;
                densMax = 0.99771554 + 0.0116776 * concTemp - 0.000020481759 * concTemp * concTemp;
                densMin = 0.99649872 + 0.011588787 * concTemp - 0.000019665877 * concTemp * concTemp;
            }
            else if (temperature >= 20.0 && temperature < 40.0)
            {
                tMin = 20.0; tMax = 40.0;
                densMax = 0.99649872 + 0.011588787 * concTemp - 0.000019665877 * concTemp * concTemp;
                densMin = 0.9901222 + 0.011296741 * concTemp - 0.000016965779 * concTemp * concTemp;
            }
            else if (temperature >= 40.0 && temperature < 60.0)
            {
                tMin = 40.0; tMax = 60.0;
                densMax = 0.9901222 + 0.011296741 * concTemp - 0.000016965779 * concTemp * concTemp;
                densMin = 0.98103014 + 0.01112983 * concTemp - 0.000015671437 * concTemp * concTemp;
            }
            else if (temperature >= 60.0 && temperature < 80.0)
            {
                tMin = 60.0; tMax = 80.0;
                densMax = 0.98103014 + 0.01112983 * concTemp - 0.000015671437 * concTemp * concTemp;
                densMin = 0.96972153 + 0.011056325 * concTemp - 0.00001533371 * concTemp * concTemp;
            }
            else // 80..100
            {
                tMin = 80.0; tMax = 100.0;
                densMax = 0.96972153 + 0.011056325 * concTemp - 0.00001533371 * concTemp * concTemp;
                densMin = 0.95682375 + 0.011033307 * concTemp - 0.000015281934 * concTemp * concTemp;
            }

            dens = densMax - (densMax - densMin) *
                  ((temperature - tMin) / (tMax - tMin));

            return dens;
        }

        /// <summary>
        /// CHEB – поліном Чебишева.
        /// </summary>
        public static double CHEB(int n, double x)
        {
            double a = 1.0;
            double b = x;
            double c;

            if (n == 0) return a;
            if (n == 1) return b;

            for (int i = 2; i <= n; i++)
            {
                c = 2.0 * x * b - a;
                a = b;
                b = c;
            }

            return b;
        }

        /// <summary>
        /// PSAT – тиск насиченої пари води при T, bar(abs).
        /// t - температура в °C
        /// (в PLC результат: 22.12E6 * EXP(BETA))
        /// </summary>
        private static readonly double[] K =
        {
            -4.059682,
             5.132256,
            -1.184241,
             0.1177959,
            -0.005157642,
            -0.001468954,
             0.0005362282,
             0.000124554,
            -4.915429E-005,
             4.630257E-005,
             1.530133E-005,
            -2.095453E-005
        };

        public static double PSAT(double t)
        {
            double k0 = 2.0;
            double k1 = 0.95;
            double k2 = 1.452207;
            double k3 = -0.8487895;

            double u = ((k0 * Math.Pow(647.3 / t - k1, 0.4)) - k2) / k3;

            double beta = 0.0f;
            for (int i = 0; i <= 11; i++)
                beta += K[i] * CHEB(i, u);

            if (beta > 100.0f)
                beta = 10.0f;

            return 22.12e6f * Math.Exp(beta); // Pa
        }

        /// <summary>
        /// TSAT – температура насичення (°C) при PS bar(abs).
        /// AX, BX – початковий інтервал по T, °C (як у TSAT),
        /// TOL – допуск по T, К.
        /// Реалізація – Brent-подібний метод для розв'язання PSAT(T) = P1.
        /// </summary>
        public static double TSAT(double ps, double ax = 50.0, double bx = 200.0, double tol = 0.1)
        {
            //// У FC1053: P1 := 100000 + PS * 100000 (Па)

            //return outT;
            double eps = 2.980232E-008f;

            double p1 = ps * 100000.0f;   // Pa
            double a = ax + 273.15f;                        // K
            double b = bx + 273.15f;                        // K

            double fa = p1 - PSAT(a);
            double fb = p1 - PSAT(b);

            double c = a, fc = fa;
            double d = b - a, e = d;
            double outT = b;

            // PLC має WHILE TRUE; тут додав ліміт, щоб у C# не зависнути назавжди
            for (int iter = 0; iter < 2000; iter++)
            {
                double xx = Math.Abs(fc);
                double yy = Math.Abs(fb);

                if (xx < yy)
                {
                    // Точно як у SCL: послідовні присвоєння (не swap!)
                    a = b; b = c; c = a;
                    fa = fb; fb = fc; fc = fa;
                }

                xx = Math.Abs(b);
                double tol1 = 2.0f * eps * xx + tol / 2.0f;
                double xm = (c - b) / 2.0f;

                if (Math.Abs(xm) <= tol1 || fb == 0.0f)
                {
                    outT = b - 273.15f;
                    break;
                }

                xx = Math.Abs(e);
                yy = Math.Abs(fa);
                double zz = Math.Abs(fb);

                double p, q, r, s;

                if (xx < tol1 || yy <= zz)
                {
                    d = xm;
                    e = d;
                }
                else
                {
                    s = fb / fa;

                    if (a == c)
                    {
                        p = 2.0f * xm * s;
                        q = 1.0f - s;
                    }
                    else
                    {
                        q = fa / fc;
                        r = fb / fc;
                        p = s * (2.0f * xm * q * (q - r) - (b - a) * (r - 1.0f));
                        q = (q - 1.0f) * (r - 1.0f) * (s - 1.0f);
                    }

                    if (p > 0.0f) q = -q;
                    else p = -p;

                    xx = Math.Abs(tol1 * q);
                    yy = Math.Abs(e * q / 2.0f);

                    if ((2.0f * p) >= (3.0f * xm * q - xx) || (p >= yy))
                    {
                        d = xm;
                        e = d;
                    }
                    else
                    {
                        e = d;
                        d = p / q;
                    }
                }

                a = b;
                fa = fb;

                if (Math.Abs(d) > tol1)
                    b = b + d;
                else
                    b = b + tol1 * Math.Sign(xm);

                fb = p1 - PSAT(b);

                // SIGN(FC) як у SCL і перевірка fb*sign(fc)>0
                float signFc = (fc > 0.0f) ? 1.0f : (fc < 0.0f ? -1.0f : 0.0f);

                if (fb * signFc > 0.0f)
                {
                    c = a;
                    fc = fa;
                    d = b - a;
                    e = d;
                }
            }

            return outT;
        }

    }
}
