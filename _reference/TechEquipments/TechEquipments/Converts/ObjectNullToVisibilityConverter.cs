using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Visibility = Visible, если object != null.
    /// Иначе Collapsed.
    /// </summary>
    public sealed class ObjectNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}