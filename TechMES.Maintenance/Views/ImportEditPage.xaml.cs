using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Страница ручного импорта и редактирования справочников Info-модуля.
/// </summary>
public partial class ImportEditPage : MaintenancePageControl
{
    public ImportEditPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Открывает список ComboBox сразу после перехода ячейки
    /// в режим редактирования.
    ///
    /// Первый клик выделяет ячейку/строку.
    /// Второй клик включает редактирование и открывает список.
    /// </summary>
    private void OnOrderLookupComboBoxLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        _ = comboBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
            }));
    }
}