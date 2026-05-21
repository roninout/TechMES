using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TechEquipments
{
    /// <summary>
    /// Value -> "0"/"1" (или число) для текста внутри прямоугольника.
    /// </summary>
    public sealed class DiDoValueToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return b ? "on" : "off";

            return System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
