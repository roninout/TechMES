using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TechEquipments
{
    /// <summary>
    /// Value -> Brush для прямоугольника:
    /// true / !=0 => зелёный, иначе серый.
    /// </summary>
    public sealed class DiDoValueToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var on = ToBool01(value);

            var brush = on
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C))  // green
                : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)); // gray

            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static bool ToBool01(object? v)
        {
            try
            {
                if (v is bool b) return b;

                if (v is int i) return i != 0;
                if (v is long l) return l != 0;
                if (v is double d) return Math.Abs(d) > 1e-12;

                var s = System.Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (bool.TryParse(s, out var bb)) return bb;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ii)) return ii != 0;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) return Math.Abs(dd) > 1e-12;

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
