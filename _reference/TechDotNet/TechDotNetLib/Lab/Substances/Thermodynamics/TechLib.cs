using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechDotNetLib.Lab.Substances
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
            double beta = (p * 100000.0 /*+ 100000.0*/) / 22.12e6;

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
        /// VG – питомий об'єм метану, м³/кг
        /// </summary>
        public static double VG(double p, double t)
        {
            double temp = 193.1718 * (p /*+ 1.0*/) / (t + 273.15);
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
            double temp = 352.65 * (p * 0.986923 /*+ 1.0*/) / (t + 273.15);
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
            //double k0 = 2.0;
            //double k1 = 0.95;
            //double k2 = 1.452207;
            //double k3 = -0.8487895;

            //double tK = t + 273.15;
            //double u = ((k0 * Math.Pow(647.3 / tK - k1, 0.4)) - k2) / k3;

            //double beta = 0.0;
            //for (int i = 0; i <= 11; i++)
            //{
            //    beta += K[i] * CHEB(i, u);
            //}

            //if (beta > 100.0)
            //    beta = 10.0;

            //return 22.12e6 * Math.Exp(beta); // Па
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
            //double pTarget = /*100000.0 +*/ ps * 100000.0;

            //// Робимо функцію f(TK) = P1 - Psat(TK-273.15)
            //double F(double tK) => pTarget - PSAT(tK - 273.15);

            //double a = ax + 273.15;
            //double b = bx + 273.15;

            //double fa = F(a);
            //double fb = F(b);

            //if (Math.Sign(fa) == Math.Sign(fb))
            //    throw new ArgumentException("Tsat: initial bracket does not bracket a root.");

            //double c = a, fc = fa;
            //double d = b - a, e = d;

            //const double eps = 2.980232e-8; // як у FC1053
            //double outT = b;

            //while (true)
            //{
            //    if (Math.Abs(fc) < Math.Abs(fb))
            //    {
            //        a = b; b = c; c = a;
            //        fa = fb; fb = fc; fc = fa;
            //    }

            //    double tol1 = 2.0 * eps * Math.Abs(b) + tol / 2.0;
            //    double xm = 0.5 * (c - b);

            //    if (Math.Abs(xm) <= tol1 || fb == 0.0)
            //    {
            //        outT = b - 273.15;
            //        break;
            //    }

            //    double s, p, q;
            //    if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
            //    {
            //        // Інтерполяція
            //        s = fb / fa;
            //        if (a == c)
            //        {
            //            p = 2.0 * xm * s;
            //            q = 1.0 - s;
            //        }
            //        else
            //        {
            //            double r = fb / fc;
            //            double t = fa / fc;
            //            p = s * (2.0 * xm * t * (t - r) - (b - a) * (r - 1.0));
            //            q = (t - 1.0) * (r - 1.0) * (s - 1.0);
            //        }

            //        if (p > 0.0) q = -q;
            //        p = Math.Abs(p);

            //        double min1 = 3.0 * xm * q - Math.Abs(tol1 * q);
            //        double min2 = Math.Abs(e * q);

            //        if (2.0 * p < (min1 < min2 ? min1 : min2))
            //        {
            //            e = d;
            //            d = p / q;
            //        }
            //        else
            //        {
            //            d = xm;
            //            e = d;
            //        }
            //    }
            //    else
            //    {
            //        d = xm;
            //        e = d;
            //    }

            //    a = b;
            //    fa = fb;

            //    if (Math.Abs(d) > tol1)
            //        b += d;
            //    else
            //        b += Math.Sign(xm) * tol1;

            //    fb = F(b);

            //    if (fb * fc > 0.0)
            //    {
            //        c = a;
            //        fc = fa;
            //        d = b - a;
            //        e = d;
            //    }
            //}

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
