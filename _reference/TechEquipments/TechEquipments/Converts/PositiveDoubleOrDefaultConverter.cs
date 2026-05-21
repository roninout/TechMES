using System;
using System.Globalization;
using System.Windows.Data;

namespace TechEquipments
{
    /// <summary>
    /// Возвращает положительное число.
    /// Если значение пустое / 0 / отрицательное / не парсится,
    /// возвращает fallback из ConverterParameter или 1000 по умолчанию.
    /// </summary>
    public sealed class PositiveDoubleOrDefaultConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double fallback = 1000.0;

            try
            {
                if (parameter != null)
                {
                    var sParam = System.Convert.ToString(parameter, CultureInfo.InvariantCulture);
                    if (double.TryParse(sParam, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFallback) &&
                        parsedFallback > 0)
                    {
                        fallback = parsedFallback;
                    }
                }

                if (value == null)
                    return fallback;

                if (value is double d && d > 0)
                    return d;

                if (value is float f && f > 0)
                    return (double)f;

                if (value is int i && i > 0)
                    return (double)i;

                if (value is long l && l > 0)
                    return (double)l;

                var s = System.Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                if (!string.IsNullOrWhiteSpace(s) &&
                    double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                {
                    return parsed;
                }

                return fallback;
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