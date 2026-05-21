using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TechEquipments.Views.Message
{
    public partial class MessageTabHost : UserControl
    {
        public MessageTabHost()
        {
            InitializeComponent();
        }

        private MainWindow? Host => Window.GetWindow(this) as MainWindow;

        private void MessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Host?.Message_SelectedMessageChanged();
        }

        private async void Show_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Message_ToggleShowAllAsync();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            Host?.Message_AddNew();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            Host?.Message_BeginEditSelected();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Message_SaveSelectedAsync();
        }

        private async void ActivityToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (Host != null)
                await Host.Message_ToggleActivitySelectedAsync();
        }

        private async void ActivityToggle_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space && e.Key != Key.Enter)
                return;

            e.Handled = true;

            if (Host != null)
                await Host.Message_ToggleActivitySelectedAsync();
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Message_DeleteSelectedAsync();
        }
    }
}