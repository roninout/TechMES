using System;

namespace TechEquipments
{
    [TrendItem("Man", YMin = 0, YMax = 1.1)]
    [TrendSeriesStyle("Man", "CornflowerBlue", transparency: 0.7)]

    [TrendItem("Mode", YMin = 0, YMax = 1.5)]
    [TrendSeriesStyle("Mode", "Green", transparency: 0.7)]

    [TrendItem("AlarmA", YMin = 0, YMax = 2)]
    [TrendSeriesStyle("AlarmA", "Red", transparency: 0.7)]

    public class MotorParam
    {
        public bool Mode { get; set; }
        public bool Auto { get; set; }
        public bool Man { get; set; }
        public bool AlarmAEn { get; set; }
        public bool AlarmA { get; set; }
        public bool TimeWorkAlarmW { get; set; }
        public bool TimeWorkAlarmWAck { get; set; }
        public bool TimeReset { get; set; }
        public bool On { get; set; }
        public bool NotTrip { get; set; }

        public int STW { get; set; }
        public int State { get; set; }
        public double TimeWarn { get; set; }
        public double TimeSet { get; set; }
        public double TimeHmi { get; set; }

        public long TimeWork { get; set; }

        public uint HashCode { get; set; }

    }
}
