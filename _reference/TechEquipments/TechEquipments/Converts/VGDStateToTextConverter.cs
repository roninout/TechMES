using System;
using System.Globalization;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Конвертирует State (int) в текст:
    /// 0 closed
    /// 1 opened
    /// 2 alarm opening
    /// 3 alarm closure
    /// 4 opening
    /// 5 closing
    /// </summary>
    public sealed class VGDStateToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int s;
            try
            {
                if (value == null) return "";
                s = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return value?.ToString() ?? "";
            }

            return s switch
            {
                0 => "closed",
                1 => "opened",
                2 => "alarm opening",
                3 => "alarm closure",
                4 => "opening",
                5 => "closing",
                _ => $"unknown ({s})"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing; // поле read-only
    }
}