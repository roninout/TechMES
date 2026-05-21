using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    [TrendItem("Value", YMin = -0.2, YMax = 1.2)]
    [TrendSeriesStyle("Value", "Blue", transparency: 0.7)]
    public class DOParam : IHasChanel
    {
        public bool Value { get; set; }
        public bool ValueTrue { get; set; }
        public bool ValueForced { get; set; }
        public bool ForceCmd { get; set; }
        public bool AlarmHealth { get; set; }
        public bool Reset { get; set; }

        public int STW { get; set; }

        public uint HashCode { get; set; }

        public string Chanel { get; set; } = "";
    }
}
