using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TechEquipments
{
    public sealed class EquipTypeGroupToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is EquipTypeGroup g
                ? EquipTypeRegistry.GetGroupCellBrush(g)
                : Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
