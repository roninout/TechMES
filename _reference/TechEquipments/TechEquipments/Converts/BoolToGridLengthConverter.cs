using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Bool -> GridLength.
    /// True => заданная ширина (по умолчанию 30), False => 0.
    /// </summary>
    public sealed class BoolToGridLengthConverter : IValueConverter
    {
        public double TrueWidth { get; set; } = 30;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool bb && bb;
            if (!b)
                return new GridLength(0);

            // можно передать width параметром: ConverterParameter="30"
            if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                return new GridLength(w);

            return new GridLength(TrueWidth);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
