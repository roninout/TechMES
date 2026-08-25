using System;

namespace TechMES.Calc.Substances.Legacy
{

    // ============================================================
    // Ported formula source: LegacySubstance.cs
    // ============================================================
        //Абстрактный класс для вещества
        internal abstract class LegacySubstance
        {
            #region fields & props
            
            //Универсальная газовая постоянная Дж/(моль*К)
            protected const double R = 8.3144598;
    
            //Признак агрегатного состояния пропилена в точке измерения
            protected bool isSteam;
    
            //Молярная масса вещества
            public abstract double MolarMass { get; }
    
            //Свойство, определяющее в каком агрегатном состоянии вещество находится (isSteam = true - газ; isSteam = false - жидкость)
            public abstract bool IsSteam { get; }
            #endregion
    
    
            #region Methods
            //Метод для определения плотности вещества при 100% концентрации
            public abstract double GetDensity(float temperature, float pressure);
    
            //Метод для определения теплоемкости вещества при 100% концентрации
            public abstract double GetCapacity(float temperature);
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public abstract double GetContent(float temperature, float pressure);
    
            #endregion
    
            protected LegacySubstance(bool isSteam)
            {
                this.isSteam = isSteam;
            }
    
    
    
    
        }

    // ============================================================
    // Ported formula source: TechLib.cs
    // ============================================================
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

    // ============================================================
    // Ported formula source: Acetaldehyde.cs
    // ============================================================
        class Acetaldehyde : LegacySubstance
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

    // ============================================================
    // Ported formula source: Acetonitrile.cs
    // ============================================================
        internal class Acetonitrile : LegacySubstance
        {
            
            #region fields & props
    
            private const double molarMass = 41.0524;        
    
            //Молярная масса ацетонитрила
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния ацетонитрила в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Acetonitrile(bool _isSteam) : base(_isSteam)
            {
                
            }
    
            #region methods
    
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
                    a0 = 803.07;
                    a1 = -1.0542;
    
                    //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                    density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
                else //Газ
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
                        
                    }
                }
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
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
                    a0 = 2.1864307;
                    a1 = 0.0015649999;
                    a2 = 0.0000083021163;                
                }
                else
                {//Газ
    
                    a0 = 1.2125728;
                    a1 = 0.0022147106;
                    a2 = 0.0000024869344;
                    a3 = -0.000000025107206;
                    a4 = 5.9195896E-11;
                    a5 = 0.0;                
                }
    
                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                //return (temperature - WspLib.Tsat(pressure)) * 100 / (1670.409 / (5.37229 - Math.Log10((pressure) * 0.98717)) - 232.959 - WspLib.Tsat(pressure));
                return (temperature - TechLib.TSAT(pressure)) * 100 / (1670.409 / (5.37229 - Math.Log10((pressure) * 0.98717)) - 232.959 - TechLib.TSAT(pressure));
            }
    
            //Расчет давления насыщенного пара при заданной температуре, бар, абс.
            private double GetPressure(double temperature)
            {
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
    
                double a0 = 0.036484162;
                double a1 = 0.0013598701;
                double a2 = 0.000067036419;
                double a3 = 0.000000064375591;
                double a4 = 8.6595042E-09;
                double a5 = 0.0;
    
                double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                
                return pressureSaturation;
            }
    
            
    
            #endregion
    
        }

    // ============================================================
    // Ported formula source: Alcohol.cs
    // ============================================================
        internal class Alcohol : LegacySubstance
        {
            
            #region fields & props
    
            private const double molarMass = 41.0524;        
    
            //Молярная масса ацетонитрила
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния ацетонитрила в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Alcohol(bool _isSteam) : base(_isSteam)
            {
                
            }
    
            #region methods
    
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
    
                if (!this.isSteam) // ---- Liquid ----
                {
                    a0 = 803.07;
                    a1 = -1.0542;
    
                    //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                    density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
                else // ---- Vapor: ideal gas ----
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try // ---- Vapor: Peng–Robinson EOS ----
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
                }
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam) // ---- Liquid ----
                { 
                    //y = a2*x^2 + a1*x + a0
                    //a0 = 2.1864307;
                    //a1 = 0.0015649999;
                    //a2 = 0.0000083021163;
    
                    //y = a0 + a1 * x + a2 * x^2
                    a0 = 2.2891429;
                    a1 = 0.0095564286;
                    a2 = 0.000026964286;
    
                    capacity = a0 + a1 * temperature + a2 * Math.Pow(temperature, 2);
                    return capacity;
                }
                else // ---- Vapor: ideal gas ----
                {
    
                    a0 = 1.2125728;
                    a1 = 0.0022147106;
                    a2 = 0.0000024869344;
                    a3 = -0.000000025107206;
                    a4 = 5.9195896E-11;
                    a5 = 0.0;
    
                    capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                    return capacity;
                }
    
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                //return capacity;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                double content;
                double alcMass;
                double a0 = -0.071728663;
                double a1 = 1.2743981;
                double a2 = 0.001897273;
                double a3 = 8.29E-06; //0.00000829;
    
                // Масовий вміст алкоголю
                //alcMass = (temperature - WspLib.Tsat((float)pressure)) * 100.0 / (1670.409 / (5.37229 - Math.Log((float)(pressure) * 0.98717) * 0.434294) - 232.959 - WspLib.Tsat((float)pressure));
                alcMass = (temperature - TechLib.TSAT((float)pressure)) * 100.0 / (1670.409 / (5.37229 - Math.Log((float)(pressure) * 0.98717) * 0.434294) - 232.959 - TechLib.TSAT((float)pressure));
    
                // Обмеження 0.0 - 100.0
                alcMass = Math.Max(0, Math.Min(100.0, alcMass));
    
                content = a0 + a1 * alcMass - a2 * Math.Pow(alcMass, 2) - a3 * Math.Pow(alcMass, 3);
                return content; 
            }
    
            //Расчет давления насыщенного пара при заданной температуре, бар, абс.
            private double GetPressure(double temperature)
            {
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
    
                double a0 = 0.036484162;
                double a1 = 0.0013598701;
                double a2 = 0.000067036419;
                double a3 = 0.000000064375591;
                double a4 = 8.6595042E-09;
                double a5 = 0.0;
    
                double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                
                return pressureSaturation;
            }
    
            
    
            #endregion
    
        }

    // ============================================================
    // Ported formula source: Butadiene_1_2.cs
    // ============================================================
        class Butadiene_1_2 : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 54.0904;
    
            //Молярная масса бутадиена 1 2
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния бутадиена 1 2 в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Butadiene_1_2(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
    
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 109750;
                    a1 = -2425.1;
                    a2 = 12.655;
                    a3 = 0.059068;
                    a4 = -0.00014415;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
    
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)  
    
                    a0 = 0.86492;
                    a1 = 0.22148;
                    a2 = 452;
                    a3 = 0.28373;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
    
            #endregion
        }

    // ============================================================
    // Ported formula source: Butadiene_1_3.cs
    // ============================================================
        class Butadiene_1_3 : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 54.0904;
    
            //Молярная масса бутадиена 1 3
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния бутадиена 1 3 в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Butadiene_1_3(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 88166;
                    a1 = 583.44;
                    a2 = 1.8231;
                    a3 = 0.030118;
                    a4 = -0.000025695;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
    
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;            
    
                double density = 0.0;
    
                if (!this.isSteam)
                {//Жидкость
                 //y = a/b^(1 + (1 - t/c)^d)                
                    a0 = 1.3314;
                    a1 = 0.28213;
                    a2 = 425;
                    a3 = 0.30137;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Butene_1.cs
    // ============================================================
        class Butene_1 : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 56.1063;        
    
            //Молярная масса Бутэна 1
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Бутэна 1 в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Butene_1(bool _isSteam) : base(_isSteam)
            {
            }
    
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 100270;
                    a1 = 86.345;
                    a2 = 7.7333;
                    a3 = 0.00096546;
                    a4 = 0.000020281;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)               
                    a0 = 0.98;
                    a1 = 0.25169;
                    a2 = 419.54;
                    a3 = 0.26645;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Cis_2_Butene.cs
    // ============================================================
        class Cis_2_Butene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 56.1063;
    
            //Молярная масса Cis_2_Butene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Cis_2_Butene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Cis_2_Butene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 79532;
                    a1 = 110.96;
                    a2 = 9.7654;
                    a3 = -0.0036798;
                    a4 = 0.000019578;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)              
                    a0 = 1.1591;
                    a1 = 0.27085;
                    a2 = 435.5;
                    a3 = 0.28116;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Diesel.cs
    // ============================================================
        internal class Diesel : LegacySubstance
        {
            
            #region fields & props
    
            private const double molarMass = 12.01070;
    
            // Молярна маса diesel
            public override double MolarMass => molarMass;
    
            // Ознака агрегатного стану diesel у точці вимірювання
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Diesel(bool _isSteam = false) : base(_isSteam) // diesel - завжди рідина!!!
            {
    
            }
    
            #region methods
    
            // Метод для визначення густини речовини при 100% концентрації, кг/м3
            public override double GetDensity(float temperature, float pressure)
            {
                //double a0 = 0.0;
                //double a1 = 0.0;
                //double a2 = 0.0;
                //double a3 = 0.0;
                //double a4 = 0.0;
                //double a5 = 0.0;
    
                //double density = 0.0;
    
                //if (temperature < 78.2)
                //{
                //    a0 = 806.08;
                //    a1 = -0.8158;
                //    a2 = -0.0002567;
                //    a3 = -0.000008873;
    
                //}
                //else
                //{
                //    a0 = 775.2;
                //    a1 = 0.2803;
                //    a2 = -0.01468;
                //    a3 = 0.00007474;
                //    a4 = -0.0000001793;
    
                //}
                ////y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                //double q15 = 860.0; // kg/m3
                double q20 = 856.5; // kg/m3
                double c0 = 0.7;    // kg/m3
    
                double density = 0.0;
    
                density = q20 - (temperature - 20.0) * c0;
                return density;
            }
    
            // Метод для визначення теплоємності речовини при 100% концентрації, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (temperature < 78.2)
                {
                    a0 = 2268.83;
                    a1 = 11.78;
                    a2 = 0.03051;
                    a3 = -0.0006118;
                    a4 = 0.000002707;
    
                }
                else
                {
                    a0 = 3774.52;
                    a1 = -39.65;
                    a2 = 0.6675;
                    a3 = -0.003946;
                    a4 = 0.000008637;
    
                }
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                capacity = (a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0) * 0.001;
                return capacity;
            }
    
            // Метод для визначення концентрації речовини в N-компонентній суміші
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
    
    
        }

    // ============================================================
    // Ported formula source: Ethane.cs
    // ============================================================
        class Ethane : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 30.0690;
    
            //Молярная масса Ethane
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Ethane в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
            public Ethane(bool _isSteam) : base(_isSteam)
            {
            }
    
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 68726;
                    a1 = -1953.6;
                    a2 = 31.772;
                    a3 = -0.10571;
                    a4 = 0.00019673;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
    
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)        
                    a0 = 1.3749;
                    a1 = 0.23949;
                    a2 = 305.43;
                    a3 = 0.22875;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Ethanol.cs
    // ============================================================
        internal class Ethanol : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 46.06804;
    
            //Молярная масса этанола
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния этанола в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Ethanol(bool _isSteam = false) : base(_isSteam) //Этанол - всегда жидкость!!!
            {
    
            }
    
            #region methods
    
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
    
                if (temperature < 78.2)
                {
                    a0 = 806.08;
                    a1 = -0.8158;
                    a2 = -0.0002567;
                    a3 = -0.000008873;                
    
                }
                else
                {
                    a0 = 775.2;
                    a1 = 0.2803;
                    a2 = -0.01468;
                    a3 = 0.00007474;
                    a4 = -0.0000001793;
    
                }
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (temperature < 78.2)
                {                
                    a0 = 2268.83;
                    a1 = 11.78;
                    a2 = 0.03051;
                    a3 = -0.0006118;
                    a4 = 0.000002707;                
    
                }
                else
                {
                    a0 = 3774.52;
                    a1 = -39.65;
                    a2 = 0.6675;
                    a3 = -0.003946;
                    a4 = 0.000008637;
                    
                }
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                capacity = (a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0) * 0.001;
                return capacity;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
    
    
        }

    // ============================================================
    // Ported formula source: Ethylene.cs
    // ============================================================
        class Ethylene : LegacySubstance
        {
    
            #region fields & props
    
            private const double molarMass = 28.0532;
    
            //Молярная масса Ethylene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Ethylene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Ethylene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 68016;
                    a1 = -22414;
                    a2 = 286.75;
                    a3 = -1.1802;
                    a4 = 0.0017304;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)             
                    a0 = 2.3782;
                    a1 = 0.29542;
                    a2 = 282.36;
                    a3 = 0.32456;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Freezium.cs
    // ============================================================
        class Freezium : LegacySubstance
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

    // ============================================================
    // Ported formula source: Fusel.cs
    // ============================================================
        internal class Fusel : LegacySubstance
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

    // ============================================================
    // Ported formula source: HCL.cs
    // ============================================================
        internal class HCL : LegacySubstance
        {
    
            #region fields & props
    
            private const double molarMass = 36.46000;
    
            // Молярна маса diesel
            public override double MolarMass => molarMass;
    
            // Ознака агрегатного стану diesel у точці вимірювання
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public HCL(bool _isSteam = false) : base(_isSteam) // HCL - завжди рідина!!!
            {
    
            }
    
            #region methods
    
            // Метод для визначення густини речовини при 100% концентрації, кг/м3
            public override double GetDensity(float temperature, float pressure)
            {
                //double a0 = 0.0;
                //double a1 = 0.0;
                //double a2 = 0.0;
                //double a3 = 0.0;
                //double a4 = 0.0;
                //double a5 = 0.0;
    
                //double density = 0.0;
    
                //if (temperature < 78.2)
                //{
                //    a0 = 806.08;
                //    a1 = -0.8158;
                //    a2 = -0.0002567;
                //    a3 = -0.000008873;
    
                //}
                //else
                //{
                //    a0 = 775.2;
                //    a1 = 0.2803;
                //    a2 = -0.01468;
                //    a3 = 0.00007474;
                //    a4 = -0.0000001793;
    
                //}
                ////y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                //double q15 = 860.0; // kg/m3
                double q20 = 856.5; // kg/m3
                double c0 = 0.7;    // kg/m3
    
                double density = 0.0;
    
                density = q20 - (temperature - 20.0) * c0;
                return density;
            }
    
            // Метод для визначення теплоємності речовини при 100% концентрації, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (temperature < 78.2)
                {
                    a0 = 2268.83;
                    a1 = 11.78;
                    a2 = 0.03051;
                    a3 = -0.0006118;
                    a4 = 0.000002707;
    
                }
                else
                {
                    a0 = 3774.52;
                    a1 = -39.65;
                    a2 = 0.6675;
                    a3 = -0.003946;
                    a4 = 0.000008637;
    
                }
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                capacity = (a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0) * 0.001;
                return capacity;
            }
    
            // Метод для визначення концентрації речовини в N-компонентній суміші
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
    
    
        }

    // ============================================================
    // Ported formula source: HydrohenPeroxyde.cs
    // ============================================================
        internal class HydrohenPeroxyde : LegacySubstance
        {
            #region fields & props
            private const double molarMass = 34.015;
    
            //Молярная масса перекиси водорода
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния перекиси водорода в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
    
            public HydrohenPeroxyde(bool _isSteam) : base(_isSteam)
            {
    
            }
    
            #region methods
    
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
                    a0 = 1471.4234;
                    a1 = -1.1229705;
                    a2 = -0.00043327967;
                    a3 = -0.00000072845085;
    
                    //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                    density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
                else //Газ
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
                }
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
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
                    a0 = 2.4605939;
                    a1 = 0.0021372924;
                    a2 = 0.0;
                    a3 = 0.0;
                    a4 = 0.0;
                    a5 = 0.0;
    
                }
                else
                {//Газ
    
                    a0 = 1.2117451;
                    a1 = 0.0011298187;
                    a2 = 0.0000024125834;
                    a3 = -0.000000016911386;
                    a4 = 3.1232139E-11;
                    a5 = 0.0;
                }
                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            //Расчет давления насыщенного пара при заданной температуре, бар, абс.
            private double GetPressure(double temperature)
            {
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
    
                double a0 = 1.310908;
                double a1 = -0.038738568;
                double a2 = 0.00047857264;
                double a3 = -0.0000029883632;
                double a4 = 9.5284911E-09;
                double a5 = 0.0;
    
                double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                return pressureSaturation;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
        }

    // ============================================================
    // Ported formula source: Isobutane.cs
    // ============================================================
        class Isobutane : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 58.1222;
    
            //Молярная масса Isobutane
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Isobutane в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Isobutane(bool _isSteam) : base(_isSteam)
            {
            }
    
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 39746.03;
                    a1 = -371.573;
                    a2 = 12.02593;
                    a3 = 0.000755039;
                    a4 = -2.59608E-07;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)         
                    a0 = 0.89934;
                    a1 = 0.25371;
                    a2 = 407.85;
                    a3 = 0.25125;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Methan.cs
    // ============================================================
        internal class Methan : LegacySubstance
        {
            #region fields & props
            private const double molarMass = 0.0160425; // kg/mol
    
            //Молярная масса
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Methan(bool _isSteam) : base(_isSteam)
            {
    
            }
            #region methods
    
            //Метод для определения плотности вещества при 100% концентрации, кг/м3
            public override double GetDensity(float temperature, float pressure)
            {
                double r = 8.314462618;
                double z = 0.0;
                double density = 0.0;
    
                double x = (pressure - 3.05e6) / 2.95e6;        // normalized pressure
                double y = (temperature - 298.15) / 25.0;       // normalized temperature
    
                double y2 = y * y;
                double y3 = y2 * y;
    
                double x2 = x * x;
                double x3 = x2 * x;
                double x4 = x3 * x;
    
                z = (0.9358613118902501
                    + 0.018918165383247483 * y
                    - 0.0032063617804402264 * y2
                    + 0.00043677853968546534 * y3)
    
                    + x * (-0.05804905343899734
                    + 0.01824068110096073 * y
                    - 0.003409664414015895 * y2
                    + 0.0005255698704910288 * y3)
    
                    + x2 * (0.004355296859995484
                    - 0.0003333147540060496 * y
                    - 0.00023875322442202978 * y2
                    + 9.647149260019897e-05 * y3)
    
                    + x3 * (0.0004996240410321649
                    - 0.0003140087813239308 * y
                    + 9.080114116994517e-05 * y2
                    - 1.445865457607404e-05 * y3)
    
                    + x4 * (1.5293429759553303e-05
                    - 4.358153213177035e-05 * y
                    + 3.2705580661003505e-05 * y2
                    - 1.1292031709083314e-05 * y3);
    
                density = this.MolarMass * pressure / r / temperature / z;
    
                //if (!this.isSteam) //Жидкость
                //{
                //    //a0 = 1000.3916;
                //    //a1 = 0.068041205;
                //    //a2 = -0.0086770695;
                //    //a3 = 0.000070624106;
                //    //a4 = -0.00000045396011;
                //    //a5 = 1.2999754E-09;
                //    //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                //    //density = WspLib.wspDSWT(Math.Max(0, temperature) + 273.15);
                //    density = 1.0 / TechLib.VW(Math.Max(0, temperature) + 273.15);
    
                //}
                //else
                //{
                //    //Плотность газа = P * 10^2/R/T(K)
                //    //R = 8.314
                //    //T(K) = t(Cels) + 273.15
    
                //    try
                //    {
                //        //density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                //        //density = WspLib.wspDSST(temperature + 273.15);
                //        //density = WspLib.wspDPT(pressure * 100000, temperature + 273.15);
                //        //density = 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15);
                //        density = Math.Max(0.0, 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15));
    
                //    }
                //    catch (ArithmeticException)
                //    {
    
                //    }
                //}
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a = 0.0;
                double b = 0.0;
                double c = 0.0;
                double d = 0.0;
                double e = 0.0;
                
                double capacity = 0.0;
    
                double temp = temperature / 1000.0;
    
                if (temperature < 1300.0)
                { //  298 to 1300	             
                    a = -0.703029;
                    b = 108.4773;
                    c = -42.52157;
                    d = 5.862788;
                    e = 0.678565;
    
                }
                else
                {//  1300 to 6000              
                    a = 85.81217;
                    b = 11.26467;
                    c = -2.114146;
                    d = 0.138190;
                    e = -26.42221;
                }
    
                capacity = (a + b * temp + c * Math.Pow(temp, 2) + d * Math.Pow(temp, 3) + e / Math.Pow(temp, 2)) / this.MolarMass;
                return capacity;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
        }

    // ============================================================
    // Ported formula source: Methyl_Acetylene.cs
    // ============================================================
        class Methyl_Acetylene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 40.0639;
    
            //Молярная масса Methyl_Acetylene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Methyl_Acetylene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Methyl_Acetylene(bool _isSteam) : base(_isSteam)
            {
            }
    
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 34169.26;
                    a1 = -350.7621;
                    a2 = 11.18743;
                    a3 = 0.000684714;
                    a4 = -2.185041E-07;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)  
                    a0 = 1.5983;
                    a1 = 0.26361;
                    a2 = 402.4;
                    a3 = 0.27835;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: N_Butane.cs
    // ============================================================
        class N_Butane : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 58.1222;
    
            //Молярная масса N_Butane
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния N_Butane в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public N_Butane(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
    
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 44749.95;
                    a1 = -338.1412;
                    a2 = 11.81452;
                    a3 = 0.00097744;
                    a4 = -3.359129E-07;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)          
                    a0 = 1.0023;
                    a1 = 0.26457;
                    a2 = 425.17;
                    a3 = 0.27138;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: N_Pentane.cs
    // ============================================================
        class N_Pentane : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 72.1488;
    
            //Молярная масса N_Pentane
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния N_Pentane в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public N_Pentane(bool _isSteam) : base(_isSteam)
            {
            }
    
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 81062;
                    a1 = -706.86;
                    a2 = 12.962;
                    a3 = -0.000049298;
                    a4 = 2.8357E-09;
                    a5 = 0;
    
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
    
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)    
                    a0 = 0.77386;
                    a1 = 0.25574;
                    a2 = 469.7;
                    a3 = 0.26319;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
    
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: NaOH.cs
    // ============================================================
        internal class NaOH : LegacySubstance
        {
    
            #region fields & props
    
            private const double molarMass = 40.00000;
    
            // Молярна маса diesel
            public override double MolarMass => molarMass;
    
            // Ознака агрегатного стану diesel у точці вимірювання
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public NaOH(bool _isSteam = false) : base(_isSteam) // NaOH - завжди рідина!!!
            {
    
            }
    
            #region methods
    
            // Метод для визначення густини речовини при 100% концентрації, кг/м3
            public override double GetDensity(float temperature, float pressure)
            {
                //double a0 = 0.0;
                //double a1 = 0.0;
                //double a2 = 0.0;
                //double a3 = 0.0;
                //double a4 = 0.0;
                //double a5 = 0.0;
    
                //double density = 0.0;
    
                //if (temperature < 78.2)
                //{
                //    a0 = 806.08;
                //    a1 = -0.8158;
                //    a2 = -0.0002567;
                //    a3 = -0.000008873;
    
                //}
                //else
                //{
                //    a0 = 775.2;
                //    a1 = 0.2803;
                //    a2 = -0.01468;
                //    a3 = 0.00007474;
                //    a4 = -0.0000001793;
    
                //}
                ////y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                //double q15 = 860.0; // kg/m3
                double q20 = 856.5; // kg/m3
                double c0 = 0.7;    // kg/m3
    
                double density = 0.0;
    
                density = q20 - (temperature - 20.0) * c0;
                return density;
            }
    
            // Метод для визначення теплоємності речовини при 100% концентрації, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (temperature < 78.2)
                {
                    a0 = 2268.83;
                    a1 = 11.78;
                    a2 = 0.03051;
                    a3 = -0.0006118;
                    a4 = 0.000002707;
    
                }
                else
                {
                    a0 = 3774.52;
                    a1 = -39.65;
                    a2 = 0.6675;
                    a3 = -0.003946;
                    a4 = 0.000008637;
    
                }
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                capacity = (a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0) * 0.001;
                return capacity;
            }
    
            // Метод для визначення концентрації речовини в N-компонентній суміші
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
    
    
        }

    // ============================================================
    // Ported formula source: Nitrogen.cs
    // ============================================================
        internal class Nitrogen : LegacySubstance
        {
            
            #region fields & props
            private const double molarMass = 28.0134;
    
            //Молярная масса азота
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния перекиси водорода в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Nitrogen(bool _isSteam = true) : base(_isSteam) //Азот - всегда газ!!!
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
                
                double a0 = 1.0400348;
                double a1 = 0.000010607402;
                double a2 = 0.00000012332117;
                double a3 = 7.0064756E-10;
                double a4 = 6.6928819E-13;
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

    // ============================================================
    // Ported formula source: Oxygen.cs
    // ============================================================
        internal class Oxygen : LegacySubstance
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

    // ============================================================
    // Ported formula source: Propadiene.cs
    // ============================================================
        class Propadiene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 40.0639;        
    
            //Молярная масса Propadiene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Propadiene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Propadiene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 34671.52;
                    a1 = -447.4983;
                    a2 = 11.46556;
                    a3 = 0.000444481;
                    a4 = -1.470826E-07;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
    
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)   
                    a0 = 0.86549;
                    a1 = 0.19732;
                    a2 = 394;
                    a3 = 0.21029;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Propane.cs
    // ============================================================
        class Propane : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 44.0956;       
    
            //Молярная масса Propane
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Propane в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Propane(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
    
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
    
                    //Reciprocal Quadratic: y = 1 / (a + bx + cx ^ 2)
    
                    //3rd degree Polynomial Fit:  y = a + bx + cx ^ 2 + dx ^ 3...	
                    //Coefficient Data:	
                    //a = 2.4507357
                    //b = 0.007114219
                    //c = 6.59E-05
                    //d = 4.84E-07
    
                    a0 = 2.4507357;
                    a1 = 0.007114219;
                    a2 = 6.59E-05;
                    a3 = 4.84E-07;
    
    
    
                    //capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                    //capacity = 1 / (a0 + a1 * temperature + a2 * Math.Pow(temperature, 2));
                    capacity = a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0; 
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)
                    a0 = 1.3186;
                    a1 = 0.27005;
                    a2 = 369.86;
                    a3 = 0.27852;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Propylene.cs
    // ============================================================
        internal class Propylene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 42.081;
    
            //Молярная масса пропилена
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния пропилена в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Propylene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
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
                    a0 = 544.49444;
                    a1 = -1.6067697;
                    a2 = -0.0062071911;
                    a3 = 0.000066556211;
                    a4 = 0.00000085372924;
                    a5 = -0.000000024993478;
    
                    //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                    density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
                else //Газ
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / this.MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
                }
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК
            public override double GetCapacity(float temperature)
            {
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
                    a0 = 2.4662773;
                    a1 = 0.0068441815;
                    a2 = 0.000029348162;
    
                }
                else
                {//Газ
    
                    a0 = 1.4382886;
                    a1 = 0.0039457659;
                    a2 = 0.00000075251963;
                    a3 = -0.000000034652143;
                    a4 = 1.7356035E-10;
                    a5 = -3.0549926E-13;
    
                }
                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            //Расчет давления насыщенного пара при заданной температуре, бар, абс.
            private double GetPressure(float temperature)
            {
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
    
                double pressureSaturation = 0.0;
    
                if (temperature > -273.15 && temperature < -23.0)
                {
                    a0 = 4.6971864;
                    a1 = -1.5576704;
                    a2 = 0.1198551;
                    a3 = 2.7092598;
                    try
                    {
                        pressureSaturation = a0 / Math.Pow((1 + Math.Exp(a1 - a2 * temperature)), (1 / a3));
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                else if (temperature >= -23.0)
                {
                    a0 = 5.7765015;
                    a1 = 0.17458286;
                    a2 = 0.0019925466;
                    a3 = 0.0000097679541;
                    pressureSaturation = a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
    
                return pressureSaturation;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {           
                double content = 0.0;
    
                //Газ
    
                double a0 = 0.8751331;
                double a1 = -0.0074854839;
                double a2 = -0.00014890413;
                double a3 = -0.00000087120511;
                double a4 = 0.0000000083535454;
                double a5 = 0.0;
    
    
                content = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return Math.Max(0.0, content * 100.0);
            }
    
            #endregion
    
        }

    // ============================================================
    // Ported formula source: PropyleneOxyde.cs
    // ============================================================
        internal class PropyleneOxyde : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 58.08;
    
            //Молярная масса пропиленоксида
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния пропиленоксида в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public PropyleneOxyde(bool _isSteam) : base(_isSteam)
            {
    
            }
    
            #region methods
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
                    a0 = 853.7;
                    a1 = -1.22;
    
                    //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                    density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                }
                else //Газ
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
                }
    
                return density;
            }
    
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК        
            public override double GetCapacity(float temperature)
            {
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
                    a0 = 2.1013073;
                    a1 = 0.0037279583;
                    a2 = 0.000011584685;
                    a3 = 6.1272975E-15;
                    a4 = -2.4889982E-16;
                    a5 = 1.5252912E-18;
    
                }
                else
                {//Газ
    
                    a0 = 1.1479922;
                    a1 = 0.0039040574;
                    a2 = -0.0000027020205;
                    a3 = 7.9984491E-10;
                    a4 = -5.1017917E-17;
                    a5 = 4.2568435E-19;
                }
    
                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            //Расчет давления насыщенного пара при заданной температуре, бар, абс.
            private double GetPressure(double temperature)
            {
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
    
                double a0 = 0.24433327;
                double a1 = 0.011605649;
                double a2 = 0.00022534828;
                double a3 = 0.0000021758871;
                double a4 = 8.3126655E-09;
                double a5 = 0.0;
    
                double pressureSaturation = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                return pressureSaturation;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                return -1;
            }
    
            #endregion
    
        }

    // ============================================================
    // Ported formula source: Trans_2_Butene.cs
    // ============================================================
        class Trans_2_Butene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 56.1063;
            
            //Молярная масса Trans_2_Butene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Trans_2_Butene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Trans_2_Butene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
    
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 60006;
                    a1 = -649.72;
                    a2 = 12.368;
                    a3 = 0.00014661;
                    a4 = -5.1566E-08;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)  
                    a0 = 1.1523;
                    a1 = 0.27235;
                    a2 = 428.6;
                    a3 = 0.28543;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Vinylacetylene.cs
    // ============================================================
        class Vinylacetylene : LegacySubstance
        {
            #region fields & props
    
            private const double molarMass = 52.0746;        
    
            //Молярная масса Vinylacetylene
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния Vinylacetylene в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Vinylacetylene(bool _isSteam) : base(_isSteam)
            {
            }
    
            #region Methods
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                {   //Жидкость
                    //y = a0 + exp b/t + c + dt + et^2
                    a0 = 49981;
                    a1 = -581.7;
                    a2 = 12.052;
                    a3 = -0.00010825;
                    a4 = 3.173E-08;
                    a5 = 0;
                    capacity = a0 + Math.Exp(a1 / temperature + a2 + a3 * temperature + a4 * Math.Pow(temperature, 2));
                }
                else
                {//Газ
    
                    //a0 = 0.86492;
                    //a1 = 0.22148;
                    //a2 = 452;
                    //a3 = 0.28373;
                    //a4 = 1.7356035E-10;
                    //a5 = -3.0549926E-13;
                    capacity = 0.0;
                }
                
                //capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            public override double GetContent(float temperature, float pressure)
            {
                throw new NotImplementedException();
            }
    
            public override double GetDensity(float temperature, float pressure)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
                double density = 0.0;
    
                if (!this.isSteam)
                { //Жидкость
                  //y = a/b^(1 + (1 - t/c)^d)               
                    a0 = 1.2594;
                    a1 = 0.25931;
                    a2 = 454;
                    a3 = 0.29553;
    
                    density = (a0 / Math.Pow(a1, 1 + Math.Pow(1 - (temperature + 273.15) / a2, a3))) * molarMass;
                }
                else
                {//Газ
    
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                    }
                    catch (ArithmeticException)
                    {
    
                    }
    
                }
                
                return density;
            }
            #endregion
        }

    // ============================================================
    // Ported formula source: Water.cs
    // ============================================================
        internal class Water : LegacySubstance
        {
            #region fields & props
            private const double molarMass = 18.01488;
    
            //Молярная масса воды
            public override double MolarMass => molarMass;
    
            //Признак агрегатного состояния воды в точке измерения
            public override bool IsSteam => isSteam;
    
            #endregion
    
            public Water(bool _isSteam) : base(_isSteam)
            {
    
            }
    
            #region methods
    
            //Метод для определения плотности вещества при 100% концентрации, кг/м3
            public override double GetDensity(float temperature, float pressure)
            {
                //double a0 = 0.0;
                //double a1 = 0.0;
                //double a2 = 0.0;
                //double a3 = 0.0;
                //double a4 = 0.0;
                //double a5 = 0.0;
    
                double density = 0.0;
                if (!this.isSteam) //Жидкость
                {
                    //a0 = 1000.3916;
                    //a1 = 0.068041205;
                    //a2 = -0.0086770695;
                    //a3 = 0.000070624106;
                    //a4 = -0.00000045396011;
                    //a5 = 1.2999754E-09;
                    //density = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
    
                    //density = WspLib.wspDSWT(Math.Max(0, temperature) + 273.15);
                    density = 1.0 / TechLib.VW(Math.Max(0, temperature));
    
                }
                else
                {
                    //Плотность газа = P * 10^2/R/T(K)
                    //R = 8.314
                    //T(K) = t(Cels) + 273.15
    
                    try
                    {
                        //density = pressure * Math.Pow(10, 2) / (R / MolarMass) / (temperature + 273.15);
                        //density = WspLib.wspDSST(temperature + 273.15);
                        //density = WspLib.wspDPT(pressure * 100000, temperature + 273.15);
                        //density = 1.0 / TechLib.VS(pressure * 100000, temperature + 273.15);
                        density = Math.Max(0.0, 1.0 / TechLib.VS(pressure, temperature));
    
                    }
                    catch (ArithmeticException)
                    {
    
                    }
                }            
    
                return density;
            }
    
            //Метод для определения теплоемкости вещества при 100% концентрации, кДж/кг/грК       
            public override double GetCapacity(float temperature)
            {
                double a0 = 0.0;
                double a1 = 0.0;
                double a2 = 0.0;
                double a3 = 0.0;
                double a4 = 0.0;
                double a5 = 0.0;
    
    
                double capacity = 0.0;
    
                if (!this.isSteam)
                { //Жидкость                
                    a0 = 4.2149573;
                    a1 = -0.0031526187;
                    a2 = 0.00010044192;
                    a3 = -1.526484e-006;
                    a4 = 1.1975875e-008;
                    a5 = -3.5978694e-011;
    
                }
                else
                {//Газ                
                    a0 = 1.8557015;
                    a1 = 0.0030295038;
                    a2 = -0.00012286806;
                    a3 = 0.0000021805877;
                    a4 = -0.000000013160691;
                    a5 = 2.8400593E-11;
                }
    
                //y = a5*x^5 + a4*x^4 + a3*x^3 + a2*x^2 + a1*x + a0
                capacity = a5 * Math.Pow(temperature, 5) + a4 * Math.Pow(temperature, 4) + a3 * Math.Pow(temperature, 3) + a2 * Math.Pow(temperature, 2) + a1 * temperature + a0;
                return capacity;
            }
    
            //Метод для определения концентрации вещества в N-компонентной смеси
            public override double GetContent(float temperature, float pressure)
            {
                return - 1;
            }
    
    
            #endregion
        }
}
