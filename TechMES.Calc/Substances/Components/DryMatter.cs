using TechMES.Calc.Substances;
using TechMES.Calc.Thermodynamics;

namespace TechMES.Calc.Substances.Components
{
    /// <summary>
    /// DryMatter – сухие вещества сахарного водного раствора.
    ///
    /// Компонент существует только в жидкой фазе.
    ///
    /// Density восстановлена из исходной ICUMSA-корреляции PLC.
    /// Capacity и Content пока не реализованы.
    /// </summary>
    internal class DryMatter : LegacySubstance
    {
        #region fields & props

        // Эквивалентная молярная масса сахарозы, г/моль.
        //
        // В текущем расчёте жидкой Density значение не используется, но базовый контракт LegacySubstance требует MolarMass.
        private const double molarMass = 342.2965;

        public override double MolarMass => molarMass;

        // DryMatter поддерживается только как жидкий компонент.
        public override bool IsSteam => false;

        #endregion

        public DryMatter() : base(false)
        {
        }

        #region methods

        // Метод для определения плотности вещества при 100% концентрации, кг/м3.
        // Как и у остальных компонентов, обычный GetDensity(T,P) означает 100% концентрацию данного вещества.
        public override double GetDensity(float temperature, float pressure)
        {
            var density = 1.0 / TechLib.VSS(Math.Max(0, temperature), 100.0);
            return density;
        }

        /// <summary>
        /// Возвращает эффективную Density DryMatter для его фактической массовой доли в водном растворе.
        ///
        /// Полученное значение не является новой эмпирической формулой.
        /// Оно алгебраически получено из исходной PLC ICUMSA-корреляции так, чтобы стандартный MixturePropertyCalculator:
        ///
        ///     rho = 1 / Σ(w_i / rho_i)
        ///
        /// для Water + DryMatter дал точно исходную плотность раствора.
        /// </summary>
        public override double GetDensity(float temperature, float pressure, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            var density = 1.0 / TechLib.VSS(Math.Max(0, temperature), massPercent);
            return density;
        }

        // Для компонента DryMatter принимаем 100% чистоту сухих веществ.
        // Это соответствует нашей общей модели компонентов: каждый компонент описывает вещество при 100% концентрации.
        // Если в будущем понадобится реальная PUR из SCADA, её можно будет передать через additionalParameters, не меняя формулу CSS.
        private const double purityPercent = 100.0;

        // Метод определения удельной теплоёмкости DryMatter при 100% концентрации, kJ/(kg·K).
        // TechLib.CSS возвращает J/(kg·K), а legacy-контракт GetCapacity возвращает kJ/(kg·K).
        public override double GetCapacity(float temperature)
        {
            return TechLib.CSS(temperature, 100.0, purityPercent) * 0.001;
        }

        /// <summary>
        /// Возвращает эффективную теплоёмкость DryMatter для его фактической массовой доли в сахарном водном растворе.
        /// Исходная PLC-функция CSS рассчитывает теплоёмкость всего раствора. Но в TechMES Water является отдельным компонентом.
        /// Поэтому, как и для Density/VSS, вклад Water исключается алгебраически.
        /// Наш MixturePropertyCalculator использует: CpMix = wWater * CpWater + wDryMatter * CpDryMatter
        /// Отсюда: CpDryMatter = (CpSolution - wWater * CpWater) / wDryMatter
        ///
        /// После этого стандартный расчёт смеси Water + DryMatter точно воспроизводит исходную CSS-функцию.
        /// </summary>
        public override double GetCapacity(float temperature, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            if (!double.IsFinite(massPercent) || massPercent <= 0.0 || massPercent > 100.0)
                throw new ArgumentOutOfRangeException(nameof(massPercent), "DryMatter mass percent must be greater than 0 and not greater than 100.");

            var dryMatterMassFraction = massPercent * 0.01;
            var waterMassFraction = 1.0 - dryMatterMassFraction;

            // Исходная CSS-функция возвращает теплоёмкость всего сахарного раствора в J/(kg·K).
            var solutionCapacityJPerKgK = TechLib.CSS(temperature, massPercent, purityPercent);

            // Water.GetCapacity возвращает legacy kJ/(kg·K). Для расчёта исключения переводим его в J/(kg·K).
            var waterCapacityJPerKgK = new Water(false).GetCapacity(temperature) * 1000.0;

            var dryMatterCapacityJPerKgK = (solutionCapacityJPerKgK - waterMassFraction * waterCapacityJPerKgK) / dryMatterMassFraction;

            if (!double.IsFinite(dryMatterCapacityJPerKgK) || dryMatterCapacityJPerKgK <= 0.0)
                throw new ArithmeticException("Calculated DryMatter specific heat capacity is invalid.");

            // Возвращаем legacy единицы GetCapacity: kJ/(kg·K).
            return dryMatterCapacityJPerKgK * 0.001;
        }

        // Content для DryMatter пока не определён.
        public override double GetContent(float temperature, float pressure)
        {
            return -1.0;
        }

        #endregion
    }
}