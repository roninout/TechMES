using System;
using System.Globalization;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Конвертер double?/int?/string ("0"/"1") <-> bool для ToggleSwitchEdit.
    /// </summary>
    public sealed class PlcDouble01ToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is bool b) return b;

                if (value is int i) return i != 0;
                if (value is long l) return l != 0;
                if (value is double d) return d > 0.5;

                var s = System.Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
                s = s.Replace(',', '.');

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
                    return dd > 0.5;

                return false;
            }
            catch
            {
                return false;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var on = false;

                if (value is bool b) on = b;
                else on = System.Convert.ToBoolean(value, CultureInfo.InvariantCulture);

                // пишем как 1/0
                return on ? 1.0 : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }
    }
}
