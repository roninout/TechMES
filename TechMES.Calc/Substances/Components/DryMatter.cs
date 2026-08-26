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

        // Capacity для DryMatter пока не определена.
        public override double GetCapacity(float temperature)
        {
            return 0.0;
        }

        // Content для DryMatter пока не определён.
        public override double GetContent(float temperature, float pressure)
        {
            return -1.0;
        }

        #endregion
    }
}