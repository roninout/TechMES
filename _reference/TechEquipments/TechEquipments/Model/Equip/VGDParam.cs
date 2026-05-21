using System;

namespace TechEquipments
{
    [TrendItem("Man", YMin = 0, YMax = 1.1)]
    [TrendSeriesStyle("Man", "CornflowerBlue", transparency: 0.7)]

    [TrendItem("Mode", YMin = 0, YMax = 1.5)]
    [TrendSeriesStyle("Mode", "Green", transparency: 0.7)]

    [TrendItem("AlarmOpen", YMin = 0, YMax = 2)]
    [TrendSeriesStyle("AlarmOpen", "Red", transparency: 0.7)]

    [TrendItem("AlarmClose", YMin = 0, YMax = 2.5)]
    [TrendSeriesStyle("AlarmClose", "Orange", transparency: 0.7)]

    public class VGDParam
    {
        public bool Mode { get; set; }
        public bool Auto { get; set; }
        public bool Man { get; set; }
        public bool AlarmEn { get; set; }
        public bool AlarmA { get; set; }
        public bool AlarmOpen { get; set; }
        public bool AlarmClose { get; set; }
        public bool Opened { get; set; }
        public bool Closed { get; set; }
        public bool Dcs { get; set; }
        public bool NotTrip { get; set; }

        public int STW { get; set; }
        public int State { get; set; }

        public double TOpen { get; set; }
        public double TClose { get; set; }

        public uint HashCode { get; set; }

    }
}
