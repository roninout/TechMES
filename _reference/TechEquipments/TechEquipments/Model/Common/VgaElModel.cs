using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class VgaElModel : ParamModelBase<VGA_ElParam>
    {
        /// <summary>
        /// UI-обертка для VGA_EL.
        /// Поддерживает прямые страницы настроек:
        /// - PLC
        /// - DI/DO
        /// - Alarm
        /// </summary>
        public VgaElModel(VGA_ElParam param) : base(param) { }

        public override IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; }
            = new[]
            {
                ParamSettingsPage.Plc,
                ParamSettingsPage.DiDo,
                ParamSettingsPage.Alarm
            };
    }
}
