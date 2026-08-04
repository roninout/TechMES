using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TechMES.Maintenance.Converters;

/// <summary>
/// Converts the zero-based visual row index to a one-based row number.
///
/// The source value is ItemsControl.AlternationIndex.
/// WPF recalculates it when rows are sorted, filtered, inserted or removed.
/// </summary>
public sealed class RowNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int zeroBasedIndex
               && zeroBasedIndex >= 0
            ? zeroBasedIndex + 1
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }
}