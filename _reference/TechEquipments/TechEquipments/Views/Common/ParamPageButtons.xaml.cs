using System.Windows;
using System.Windows.Controls;

namespace TechEquipments.Views.Common
{
    /// <summary>
    /// Общая панель кнопок Param.
    /// Все переключение страниц делает MainWindow.
    /// </summary>
    public partial class ParamPageButtons : UserControl
    {
        public ParamPageButtons()
        {
            InitializeComponent();
        }

        private void PageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not ParamSettingsPage page)
                return;

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.ShowParamPage(page);
        }
    }
}
