using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class MotorModel : ParamModelBase<MotorParam>
    {
        public MotorModel(MotorParam param) : base(param) { }

        /// <summary>
        /// Motor уже сейчас умеет показывать несколько дополнительных секций.
        /// Дальше это можно будет использовать прямо из XAML без хардкода по типам.
        /// </summary>
        public override IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; }
            = new[]
            {
                ParamSettingsPage.Plc,
                ParamSettingsPage.DiDo,
                ParamSettingsPage.Alarm,
                ParamSettingsPage.TimeWork,
                ParamSettingsPage.DryRun,
                ParamSettingsPage.Atv
            };
    }
}
