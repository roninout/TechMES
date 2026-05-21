using DevExpress.Xpf.Core;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace TechEquipments.Views.Settings
{
    /// <summary>
    /// Модальное окно редактирования appsettings.json.
    ///
    /// Важно:
    /// - редактируем runtime-файл рядом с exe;
    /// - сохраняем как есть (включая комментарии, если они есть);
    /// - перед сохранением делаем базовую проверку через ConfigurationBuilder,
    ///   чтобы не записать совсем сломанный JSON/appsettings.
    /// </summary>
    public partial class AppSettingsWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private readonly string _settingsPath;
        private readonly Func<Task<BulkQrGenerateResult>>? _generateAllQrAsync;

        public AppSettingsWindow(string settingsPath, Func<Task<BulkQrGenerateResult>>? generateAllQrAsync = null)
        {
            InitializeComponent();

            _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
            _generateAllQrAsync = generateAllQrAsync;

            PathTextBlock.Text = $"File: {_settingsPath}";
            GenerateAllQrButton.IsEnabled = _generateAllQrAsync != null;

            LoadSettingsText();
        }

        /// <summary>
        /// Загружает текст appsettings.json в редактор.
        /// Если файла ещё нет — просто показываем пустой редактор.
        /// </summary>
        private void LoadSettingsText()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    EditorTextBox.Text = File.ReadAllText(_settingsPath, Encoding.UTF8);
                }
                else
                {
                    EditorTextBox.Text = "";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to read settings file.\n\n{ex.Message}",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Apply:
        /// 1) валидируем содержимое;
        /// 2) сохраняем в runtime appsettings.json;
        /// 3) показываем сообщение о необходимости перезапуска;
        /// 4) закрываем окно.
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var text = EditorTextBox.Text ?? string.Empty;

            try
            {
                ValidateSettingsText(text);

                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_settingsPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                DXMessageBox.Show(
                    "Settings were saved successfully.\nPlease restart the application for the changes to take effect.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(
                    $"Failed to save settings.\n\n{ex.Message}",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Проверяет, что текст можно прочитать как appsettings-конфиг.
        /// Это мягкая защита от сохранения битого файла.
        /// </summary>
        private static void ValidateSettingsText(string text)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

            var cfg = new ConfigurationBuilder()
                .AddJsonStream(ms)
                .Build();

            // Просто факт Build() уже означает, что формат читается.
            _ = cfg;
        }

        /// <summary>
        /// Массовая генерация QR-кодов по всему оборудованию.
        /// Прогресс идёт в нижнюю панель MainWindow.
        /// </summary>
        private async void GenerateAllQr_Click(object sender, RoutedEventArgs e)
        {
            if (_generateAllQrAsync == null)
                return;

            try
            {
                GenerateAllQrButton.IsEnabled = false;
                ApplyButton.IsEnabled = false;
                CancelButton.IsEnabled = false;

                var result = await _generateAllQrAsync();

                DXMessageBox.Show(
                    "QR codes generation is complete.\n\n" +
                    $"Total: {result.Total}\n" +
                    $"Created: {result.Created}\n" +
                    $"Skipped: {result.Skipped}\n" +
                    $"Failed: {result.Failed}",
                    "QR generation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(
                    $"Failed to generate QR codes.\n\n{ex.Message}",
                    "QR generation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                GenerateAllQrButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Отмена без сохранения.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}