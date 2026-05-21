using System;
using System.Globalization;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Округляет значение вверх до указанного шага.
    ///
    /// Примеры:
    /// value=5.4, step=0.5 => 5.5
    /// value=5.4, step=1.0 => 6.0
    ///
    /// Если значение некорректное, возвращает fallback = 1.0.
    /// </summary>
    public sealed class RoundUpToStepConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double fallback = 1.0;
            double step = 0.5;

            try
            {
                if (parameter != null)
                {
                    var sParam = System.Convert.ToString(parameter, CultureInfo.InvariantCulture);
                    if (double.TryParse(sParam, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedStep) &&
                        parsedStep > 0)
                    {
                        step = parsedStep;
                    }
                }

                if (value == null)
                    return fallback;

                var s = System.Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return fallback;

                if (number <= 0)
                    return fallback;

                var rounded = Math.Ceiling(number / step) * step;
                return rounded;
            }
            catch
            {
                return fallback;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}