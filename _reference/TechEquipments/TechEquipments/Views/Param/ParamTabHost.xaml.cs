using DevExpress.Xpf.Editors;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// Interaction logic for ParamTabHost.xaml
    /// </summary>
    public partial class ParamTabHost
    {
        public ParamTabHost()
        {
            InitializeComponent();
        }

        private MainWindow? Host
        {
            get
            {
                if (Window.GetWindow(this) is MainWindow mw)
                    return mw;

                return Application.Current?.MainWindow as MainWindow;
            }
        }

        /// <summary>
        /// Кнопка: генерация QR по текущему тексту поиска/выбранному оборудованию.
        /// </summary>
        private async void GenerateQr_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Param_GenerateQrAsync();
        }

        /// <summary>
        /// Кнопка: сканирование QR камерой -> ExternalTag -> поиск -> запуск Param polling.
        /// </summary>
        private async void ScanQr_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Param_ScanQrToExternalTagAndSearchAsync();
        }

        private async void FavoriteToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb)
                return;

            if (Host == null)
                return;

            await Host.Param_SetFavoriteAsync(tb.IsChecked == true);
        }
    }
}
