using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Показывает кнопку страницы Param, если текущая модель поддерживает эту страницу.
    ///
    /// Binding value: CurrentParamModel
    /// ConverterParameter: Plc / DiDo / Alarm / TimeWork / DryRun
    /// </summary>
    public class ParamModelSupportsPageToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var raw = (parameter as string ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return Visibility.Collapsed;

            if (!Enum.TryParse(raw, ignoreCase: true, out ParamSettingsPage page))
                return Visibility.Collapsed;

            // Chart всегда доступен, но этот converter для Chart обычно не нужен
            if (page == ParamSettingsPage.None)
                return Visibility.Visible;

            if (value is IParamModel model && model.SupportedPages.Contains(page))
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}