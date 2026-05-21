using System;
using System.Globalization;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Value(double?/bool/string) -> "on"/"off" (для PLC EqDigital/EqDigitalInOut).
    /// </summary>
    public sealed class PlcOnOffTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var on = ToBool01(value);
            return on ? "on" : "off";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static bool ToBool01(object v)
        {
            try
            {
                if (v is bool b) return b;
                if (v is double d) return d > 0.5;
                if (v is float f) return f > 0.5f;
                if (v is int i) return i != 0;
                var s = (v?.ToString() ?? "").Trim();
                if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    return x > 0.5;
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// PLC ValveStatus: Value(code) -> text.
    /// 0 closed, 1 opened, 2 alarm opening, 3 alarm closure, 4 opening, 5 closing
    /// </summary>
    public sealed class PlcValveStatusToTextConverter : IValueConverter
    {
        private static readonly string[] Map =
        {
            "closed", "opened", "alarm opening", "alarm closure", "opening", "closing"
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var code = ToIntCode(value);
            return (code >= 0 && code < Map.Length) ? Map[code] : code.ToString(CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static int ToIntCode(object v)
        {
            try
            {
                if (v is int i) return i;
                if (v is double d) return (int)Math.Round(d);
                var s = (v?.ToString() ?? "").Trim();
                if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    return (int)Math.Round(x);
            }
            catch { }
            return -1;
        }
    }

    /// <summary>
    /// PLC MotorStatus: Value(code) -> text.
    /// 0 stop, 1 run, 2 alarm, 3 alarm, 4 starting, 5 stopping, 6 waiting
    /// </summary>
    public sealed class PlcMotorStatusToTextConverter : IValueConverter
    {
        private static readonly string[] Map =
        {
            "stop", "run", "alarm", "alarm", "starting", "stopping", "waiting"
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var code = ToIntCode(value);
            return (code >= 0 && code < Map.Length) ? Map[code] : code.ToString(CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static int ToIntCode(object v)
        {
            try
            {
                if (v is int i) return i;
                if (v is double d) return (int)Math.Round(d);
                var s = (v?.ToString() ?? "").Trim();
                if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    return (int)Math.Round(x);
            }
            catch { }
            return -1;
        }
    }

    /// <summary>
    /// Value -> "0","1","2"... (чтобы DataTrigger сравнивался с string).
    /// </summary>
    public sealed class PlcValueToCodeStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
                if (value is double d) return ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture);

                var s = (value?.ToString() ?? "").Trim();
                if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    return ((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}