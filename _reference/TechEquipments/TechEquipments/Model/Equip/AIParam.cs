using System;

namespace TechEquipments
{
    /// <summary>
    /// Param model for Analog In equipment (AnalogInCalc).
    /// 
    /// IMPORTANT (Param chart):
    /// - Use [TrendItem] to declare which "items" (series) we show in the chart.
    /// - The FIRST [TrendItem] is treated as the base series for the common Y axis.
    /// - For other items you can optionally define YMin/YMax - the values will be linearly scaled
    ///   to the base Y axis (while tooltips/crosshair still show the raw values).
    /// - Use [TrendSeriesStyle] to set colors/opacity for each series.
    /// </summary>
    [TrendItem("R")]
    [TrendSeriesStyle("R", "#2E7D32", transparency: 0.7)] // green

    //[TrendItem("STW", YMin = 0, YMax = 100)]          // 0..100 will be stretched to base Y axis
    //[TrendSeriesStyle("STW", "DarkSlateBlue", transparency: 1.0)]
    public class AIParam : IHasUnit, IHasChanel // AnalogInCalc
    {
        public bool AlarmLAEn { get; set; }
        public bool AlarmLWEn { get; set; }
        public bool AlarmHWEn { get; set; }
        public bool AlarmHAEn { get; set; }
        public bool ForceCmd { get; set; }
        public bool NotTripLow { get; set; }
        public bool NotTripHigh { get; set; }
        public bool RealVar { get; set; }
        public bool AlarmLA { get; set; }
        public bool AlarmLW { get; set; }
        public bool AlarmHW { get; set; }
        public bool AlarmHA { get; set; }
        public bool AlarmA { get; set; }
        public bool Shunt { get; set; }
        public bool AlarmW { get; set; }
        public bool AlarmHealth { get; set; }

        public int STW { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double MinR { get; set; }
        public double MaxR { get; set; }
        public double Flt { get; set; }
        public double Coef { get; set; }
        public double Value { get; set; }
        public double Hmi { get; set; }
        public double HmiTrue { get; set; }
        public double HmiForced { get; set; }
        public double SetLA { get; set; }
        public double SetLW { get; set; }
        public double SetHW { get; set; }
        public double SetHA { get; set; }
        public double SetHyst { get; set; }
        public double R { get; set; }

        public uint HashCode { get; set; }

        /// <summary>
        /// Engineering units for the "R" item (filled manually during ReadEquipParamsAsync).
        /// This is not a real tag field (we do not write it back to SCADA).
        /// </summary>
        public string Unit { get; set; } = "";
        public string Chanel { get; set; } = "";
    }
}