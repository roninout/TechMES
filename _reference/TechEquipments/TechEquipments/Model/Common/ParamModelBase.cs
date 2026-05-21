using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Базовая обертка для новых моделей Param.
    /// </summary>
    public abstract class ParamModelBase<TParam> : IParamModel, IHasChanel, IHasUnit
        where TParam : class, new()
    {
        protected ParamModelBase(TParam param)
        {
            Param = param ?? new TParam();
        }

        /// <summary>
        /// Внутренняя "сырая" модель. Именно к ней будут обращаться текущие View через Binding Param.*.
        /// </summary>
        public TParam Param { get; }

        public object ParamObject => Param;

        /// <summary>
        /// По умолчанию у модели нет дополнительных страниц.
        /// Конкретные модели могут переопределить это.
        /// </summary>
        public virtual IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; } = Array.Empty<ParamSettingsPage>();

        public bool HasPlcPage => SupportedPages.Contains(ParamSettingsPage.Plc);
        public bool HasDiDoPage => SupportedPages.Contains(ParamSettingsPage.DiDo);
        public bool HasAlarmPage => SupportedPages.Contains(ParamSettingsPage.Alarm);
        public bool HasTimeWorkPage => SupportedPages.Contains(ParamSettingsPage.TimeWork);
        public bool HasDryRunPage => SupportedPages.Contains(ParamSettingsPage.DryRun);
        public bool HasAtvPage => SupportedPages.Contains(ParamSettingsPage.Atv);

        /// <summary>
        /// Проксируем Chanel наружу, чтобы MainWindow/Header продолжали работать как раньше.
        /// </summary>
        public string Chanel
        {
            get => Param is IHasChanel hasChanel ? hasChanel.Chanel : "";
            set
            {
                if (Param is IHasChanel hasChanel)
                    hasChanel.Chanel = value;
            }
        }

        /// <summary>
        /// Проксируем Unit наружу, чтобы существующая логика не ломалась.
        /// </summary>
        public string Unit
        {
            get => Param is IHasUnit hasUnit ? hasUnit.Unit : "";
            set
            {
                if (Param is IHasUnit hasUnit)
                    hasUnit.Unit = value;
            }
        }
    }
}
