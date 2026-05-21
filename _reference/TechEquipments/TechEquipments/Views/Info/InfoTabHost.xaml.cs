using DevExpress.Xpf.Editors;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace TechEquipments.Views.Info
{
    /// <summary>
    /// Interaction logic for InfoTabHost.xaml
    /// </summary>
    public partial class InfoTabHost : UserControl
    {
        public InfoTabHost()
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

        private async void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_BeginEditAsync();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_SaveAsync();
        }

        private async void LoadPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_LoadPhotoFilesAsync();
        }

        private async void CapturePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_CapturePhotoFromCameraAsync();
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            Host?.Info_RemoveSelectedPhoto();
        }

        private async void DeletePhotoFromDb_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_DeleteSelectedPhotoFromDbAsync();
        }

        private async void LoadDocument_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_LoadCurrentDocumentFilesAsync();
        }

        private async void RemoveDocument_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_RemoveCurrentDocumentAsync();
        }

        private async void DeleteDocumentFromDb_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_DeleteCurrentDocumentFromDbAsync();
        }

        private async void RememberPdfPosition_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_RememberCurrentDocumentPositionAsync(PdfViewer);
        }

        private async void ExportPdf_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_ExportCurrentDocumentAsync();
        }

        private async void DocumentSelectionChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            // Даём binding SelectedItem успеть зафиксировать CurrentInfoSelectedDocumentFile
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            if (Host != null)
                await Host.Info_OnCurrentDocumentSelectionChangedAsync();
        }

        private async void PdfViewer_DocumentLoaded(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_RestoreCurrentDocumentPositionAsync(PdfViewer);
        }

        private async void PageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not InfoPageKind page)
                return;

            if (Host != null)
                await Host.ShowInfoPageAsync(page);
        }

        private async void PhotoThumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnSelectedPhotoChangedAsync();
        }

        private async void PhotoLibraryCheckEdit_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not EquipmentInfoFileDto file)
                return;

            if (Host != null)
                await Host.Info_OnPhotoLibraryCheckChangedAsync(file);
        }

        private void PhotoThumbItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Не перехватываем клик по самому CheckEdit,
            // иначе чекбокс не будет нормально переключаться.
            if (IsInsideCheckEdit(e.OriginalSource as DependencyObject))
                return;

            if (sender is not ListBoxItem item)
                return;

            item.IsSelected = true;
            item.Focus();
        }

        private static bool IsInsideCheckEdit(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is CheckEdit)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private async void DocumentLibraryEditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnDocumentLibraryEditValueChangedAsync();
        }

        private async void ImportImages_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_ImportImagesFromFolderAsync();
        }

        private async void ProductCode_EditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (Host == null)
                return;

            await Host.Info_ApplyProductCodeFromUiAsync(e.NewValue?.ToString());
        }

        private void AddNote_Click(object sender, RoutedEventArgs e)
        {
            Host?.Info_AddNewNote();
        }

        private async void DeleteNote_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_DeleteSelectedNoteAsync();
        }
    }
}
