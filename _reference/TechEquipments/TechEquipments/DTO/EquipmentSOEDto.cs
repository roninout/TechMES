using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public class EquipmentSOEDto
    {
        public DateTime TimeUtc { get; init; }
        public DateTime TimeLocal => TimeUtc.ToLocalTime();

        public EquipTypeGroup TypeGroup { get; set; }        
        public string Equipment { get; init; } = "";

        public double TrnValue { get; init; }
        public long BitCode { get; init; }
        public string Event { get; set; } = "";     // то, что показываем (Description)
        public string EventKey { get; set; } = "";  // то, по чему красим (Enum name)

        public string ValueQuality { get; init; } = "";

        //public string Type { get; init; } = "";
        //public int Quality { get; init; }
        //public string ValueType { get; init; } = "";
        //public string LastValueQuality { get; init; } = "";
        //public bool Partial { get; init; }
    }
}
