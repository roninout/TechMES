using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TechMES.Maintenance.Converters;

/// <summary>
/// Возвращает визуальный номер строки DataGrid, начиная с единицы.
///
/// Важно: конвертер получает сам DataGridRow и не использует AlternationIndex.
/// Это не изменяет AlternationCount и не нарушает темозависимый стиль строк WPF UI.
/// </summary>
public sealed class RowNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,CultureInfo culture)
    {
        return value is DataGridRow row
            ? row.GetIndex() + 1
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }
}
