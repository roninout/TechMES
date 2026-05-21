using System;

namespace TechEquipments
{
    /// <summary>
    /// One point for the Param chart.
    /// 
    /// We intentionally keep two values:
    /// - Value    : value used for drawing on the common Y axis (can be scaled).
    /// - RawValue : original "real" value from the trend (used for tooltips / crosshair).
    /// </summary>
    public sealed class TrendPoint
    {
        /// <summary>Series key (e.g. "R", "STW").</summary>
        public string Series { get; set; } = string.Empty;

        /// <summary>Local time for UI.</summary>
        public DateTime Time { get; set; }

        /// <summary>Value used by the chart (can be scaled to base axis).</summary>
        public double Value { get; set; }

        /// <summary>Original value (not scaled).</summary>
        public double RawValue { get; set; }
    }
}
