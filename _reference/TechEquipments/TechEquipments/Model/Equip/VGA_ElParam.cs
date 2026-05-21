using System;

namespace TechEquipments
{
    [TrendItem("R", YMin = 0, YMax = 100)]
    [TrendSeriesStyle("R", "#4F81BD", transparency: 0.7)]

    [TrendItem("CurrPos", YMin = 0, YMax = 100)]
    [TrendSeriesStyle("CurrPos", "Gray", transparency: 0.7)]
    public class VGA_ElParam : IHasUnit
    {
        public bool Mode { get; set; }
        public bool OpenCmd { get; set; }
        public bool CloseCmd { get; set; }
        public bool AlarmEn { get; set; }
        public bool Alarm { get; set; }
        public bool SQEn { get; set; }
        public bool ActuatorEn { get; set; }
        public bool Opened { get; set; }
        public bool Closed { get; set; }
        public bool OpenAl { get; set; }
        public bool CloseAl { get; set; }

        public int State { get; set; }

        public double Man { get; set; }
        public double CurrPos { get; set; }
        public double TimeOpening { get; set; }
        public double OutMin { get; set; }
        public double OutMax { get; set; }
        public double R { get; set; }

        public long STW { get; set; }

        public uint HashCode { get; set; }

        private string _unit = "";
        public string Unit
        {
            get => _unit;
            set => _unit = Clean(value);
        }

        private static string Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Trim();

            if (s.StartsWith("@(") && s.EndsWith(")") && s.Length >= 3)
            {
                s = s.Substring(2, s.Length - 3).Trim();

                // снять кавычки, если есть
                if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                    s = s[1..^1].Trim();
            }

            return s;
        }

    }
}
