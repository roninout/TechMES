using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class VgdModel : ParamModelBase<VGDParam>
    {
        public VgdModel(VGDParam param) : base(param) { }

        public override IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; }
            = new[]
            {
                ParamSettingsPage.Plc,
                ParamSettingsPage.DiDo,
                ParamSettingsPage.Alarm
            };
    }
}
