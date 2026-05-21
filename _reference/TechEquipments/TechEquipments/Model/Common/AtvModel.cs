using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class AtvModel : ParamModelBase<AtvParam>
    {
        public AtvModel(AtvParam param) : base(param) { }

        public override IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; }
        = new[]
        {
                ParamSettingsPage.Atv,
                ParamSettingsPage.Alarm
        };
    }
}
