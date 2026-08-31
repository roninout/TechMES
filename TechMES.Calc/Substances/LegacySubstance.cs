using System.Collections.Generic;

namespace TechMES.Calc.Substances
{
    // Абстрактный класс для вещества.
    //
    // Базовые GetDensity/GetCapacity сохранены в том же виде, в каком они были в TechDotNetLib.
    // Content вынесен в отдельный IContentSubstanceModel, потому что Content-корреляция зависит не только от вещества, но и от физической системы компонентов.
    internal abstract class LegacySubstance
    {
        #region fields & props

        // Универсальная газовая постоянная Дж/(моль*К).
        protected const double R = 8.3144598;

        // Признак агрегатного состояния вещества в точке измерения.
        protected bool isSteam;

        // Молярная масса вещества.
        public abstract double MolarMass { get; }

        // Свойство, определяющее агрегатное состояние:
        // isSteam = true - газ; isSteam = false - жидкость.
        public abstract bool IsSteam { get; }

        #endregion

        #region Original TechDotNetLib methods

        // Метод для определения плотности вещества при 100% концентрации.
        public abstract double GetDensity(float temperature, float pressure);

        // Метод для определения теплоемкости вещества при 100% концентрации.
        public abstract double GetCapacity(float temperature);

        #endregion

        #region Extended overloads

        /// <summary>
        /// Расширенная перегрузка Density для будущих формул,
        /// которым кроме Temperature и Pressure нужны дополнительные ProcessInput.
        ///
        /// Все перенесённые legacy-компоненты по умолчанию попадают
        /// в исходный GetDensity(float temperature, float pressure),
        /// поэтому их формулы остаются идентичными TechDotNetLib.
        /// </summary>
        public virtual double GetDensity(float temperature, float pressure, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            return GetDensity(temperature, pressure);
        }

        /// <summary>
        /// Расширенная перегрузка Density, дополнительно получающая
        /// текущую массовую долю самого компонента в смеси.
        ///
        /// Для обычных компонентов MassPercent не требуется,
        /// поэтому базовая реализация просто вызывает предыдущую перегрузку.
        ///
        /// Этот уровень нужен для специальных корреляций, в которых
        /// физическое свойство компонента зависит от его концентрации в смеси.
        ///
        /// Первый такой компонент - DryMatter.
        /// Остальные перенесённые вещества продолжают работать без изменений.
        /// </summary>
        public virtual double GetDensity(float temperature, float pressure, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            return GetDensity(temperature, pressure, additionalParameters);
        }

        /// <summary>
        /// Расширенная перегрузка Capacity.
        /// Старые компоненты продолжают использовать исходный GetCapacity(float temperature).
        /// </summary>
        public virtual double GetCapacity(float temperature, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            return GetCapacity(temperature);
        }

        /// <summary>
        /// Расширенная перегрузка Capacity, дополнительно получающая фактическую массовую долю компонента в смеси.
        /// Большинство legacy-компонентов от концентрации не зависит, поэтому базовая реализация вызывает обычную перегрузку.
        /// DryMatter использует MassPercent для точного восстановления исходной формулы теплоёмкости сахарного раствора.
        /// </summary>
        public virtual double GetCapacity(float temperature, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
        {
            return GetCapacity(temperature, additionalParameters);
        }

        #endregion

        protected LegacySubstance(bool isSteam)
        {
            this.isSteam = isSteam;
        }
    }
}