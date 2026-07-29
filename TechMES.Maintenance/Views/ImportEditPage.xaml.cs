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

        /*
         * IMPORT является общей служебной вкладкой для всех справочников,
         * поэтому в интерфейсе она располагается после SUPPLIER, ORDERS,
         * INSTRUCTION и SCHEME. Перестановка выполняется после загрузки XAML,
         * чтобы большой блок разметки импорта оставался цельным и удобным
         * для дальнейшей реализации Excel-алгоритма.
         */
        ImportTabControl.Items.Remove(ImportTab);
        ImportTabControl.Items.Add(ImportTab);
        ImportTabControl.SelectedIndex = 0;
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
