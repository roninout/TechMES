using System;

namespace TechEquipments
{
    /// <summary>
    /// Describes one trend "item" (series) for the Param chart.
    /// 
    /// Usage example:
    ///   [TrendItem("R")]                        // base series (Y range from MinR/MaxR)
    ///   [TrendItem("STW", YMin = 0, YMax = 100)] // series that will be scaled to the base Y axis
    /// 
    /// IMPORTANT:
    /// - Attribute named arguments must be compile-time constants.
    ///   That is why YMin/YMax are doubles (NOT double?).
    /// - If YMin/YMax are not set (NaN) -> the series uses the base axis without scaling.
    /// </summary>

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TrendItemAttribute : Attribute
    {
        /// <summary>Series key used in Plant SCADA (e.g. "R", "STW").</summary>
        public string Item { get; }

        /// <summary>Optional native minimum for this series (NaN = not set).</summary>
        public double YMin { get; set; } = double.NaN;

        /// <summary>Optional native maximum for this series (NaN = not set).</summary>
        public double YMax { get; set; } = double.NaN;

        /// <summary>True when both YMin and YMax are explicitly set.</summary>
        public bool HasYRange => !double.IsNaN(YMin) && !double.IsNaN(YMax);

        public TrendItemAttribute(string item)
        {
            Item = item ?? string.Empty;
        }
    }
}
