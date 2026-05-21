using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Показывает секцию только если CurrentParamSettingsPage == ConverterParameter.
    /// Пример:
    /// Visibility="{Binding DataContext.CurrentParamSettingsPage,
    ///              RelativeSource={RelativeSource AncestorType=Window},
    ///              Converter={StaticResource ParamPageToVis},
    ///              ConverterParameter=Plc}"
    /// </summary>
    public class ParamSettingsPageToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ParamSettingsPage currentPage)
                return Visibility.Collapsed;

            var raw = (parameter as string ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return Visibility.Collapsed;

            if (!Enum.TryParse(raw, ignoreCase: true, out ParamSettingsPage targetPage))
                return Visibility.Collapsed;

            return currentPage == targetPage
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}