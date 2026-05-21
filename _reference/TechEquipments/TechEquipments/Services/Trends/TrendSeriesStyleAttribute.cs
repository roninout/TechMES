using System;

namespace TechEquipments
{
    /// <summary>
    /// Declares a visual style for a trend series (Param chart).
    /// 
    /// NOTE:
    /// - "Color" accepts "#RRGGBB", "#AARRGGBB" or a WPF known color name (e.g. "Black", "ForestGreen").
    /// - "Transparency" matches DevExpress Series.Transparency (0..1), where 1 = fully transparent.
    /// </summary>

    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
    public sealed class TrendSeriesStyleAttribute : Attribute
    {
        public string Item { get; }          // "R", "STW", ...
        public string Color { get; }         // "#RRGGBB" / "#AARRGGBB" / "ForestGreen"
        public double Transparency { get; }  // 0..1 (DevExpress Series.Transparency)

        public TrendSeriesStyleAttribute(
            string item,
            string color,
            double transparency = 0.8
            )
        {
            Item = item ?? "";
            Color = color ?? "";
            Transparency = transparency;
        }
    }
}
