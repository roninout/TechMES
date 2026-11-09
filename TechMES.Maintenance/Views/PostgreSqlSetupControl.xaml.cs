using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

/// <summary>Подготовка двух компьютеров БД, доступная также из единственной Maintenance на Primary.</summary>
public partial class PostgreSqlSetupControl : UserControl
{
    private readonly PostgreSqlSetupService _service = new();
    private bool _loadFailed;
    private bool _loaded;

    public List<PostgreSqlSetupProfile> Profiles { get; }

    /// <summary>Загружает отдельные профили подготовки, не меняя appsettings Runtime.</summary>
    public PostgreSqlSetupControl()
    {
        InitializeComponent();

        try
        {
            Profiles = _service.LoadProfiles();
        }
        catch (Exception ex)
        {
            Profiles = [new(), new() { Name = "Secondary DB", ComputerName = "192.168.1.131" }];
            _loadFailed = true;
            StatusBox.Text = "Ошибка профиля: " + ex.Message + "\nПроверьте значения и явно сохраните профили.";
        }

        DataContext = this;
    }

    /// <summary>При первом открытии показывает отчёт либо выполняет свежую проверку локального профиля.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _loadFailed)
            return;

        _loaded = true;

        try
        {
            var local = Profiles.FirstOrDefault(PostgreSqlSetupService.IsLocal);

            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            if (local is not null && admin)
            {
                ProfilesTabs.SelectedItem = local;
                await RunAsync(false);
            }
            else
            {
                ShowReceipt();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Возвращает выбранный профиль с уже обновлёнными привязками полей.</summary>
    private PostgreSqlSetupProfile SelectedProfile() => ProfilesTabs.SelectedItem as PostgreSqlSetupProfile ?? throw new InvalidOperationException("Выберите профиль PostgreSQL.");

    /// <summary>Не использует дефолтные значения повреждённого файла без явного сохранения оператором.</summary>
    private void SaveForOperation()
    {
        if (_loadFailed)
            throw new InvalidDataException("Сначала проверьте значения и нажмите «Сохранить профили».");

        if (Validation.GetHasError(ProfilesTabs) || HasValidationError(ProfilesTabs))
            throw new InvalidDataException("Исправьте ошибки ввода полей PostgreSQL.");

        _service.SaveProfiles(Profiles);
    }

    /// <summary>Проверяет ошибки привязок внутри текущего визуального содержимого вкладки.</summary>
    private static bool HasValidationError(DependencyObject parent)
    {
        if (Validation.GetHasError(parent))
            return true;

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(parent, index)))
                return true;
        }

        return false;
    }

    /// <summary>Явно сохраняет профиль, сохраняя прежний файл как .bak.</summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (HasValidationError(ProfilesTabs))
                throw new InvalidDataException("Исправьте ошибки ввода.");

            _service.SaveProfiles(Profiles);
            _loadFailed = false;
            ShowReceipt();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Отображает дату сохранённой проверки без объявления удалённого состояния текущим.</summary>
    private void ShowReceipt()
    {
        if (_loadFailed || ProfilesTabs.SelectedItem is not PostgreSqlSetupProfile profile)
            return;

        try
        {
            var path = _service.ReceiptPath(profile);
            StatusBox.Text = File.Exists(path)
                ? Describe(_service.ReadReport(profile, path))
                : "Подготовка не подтверждена для текущего профиля.\nВыполните локальную проверку или экспортируйте скрипт на сервер БД и загрузите его result.json.";
        }
        catch (Exception ex)
        {
            StatusBox.Text = "Отчёт не подтверждён: " + ex.Message;
        }
    }

    /// <summary>При выборе другого компьютера показывает только относящийся к нему отчёт.</summary>
    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ProfilesTabs))
            ShowReceipt();
    }

    /// <summary>Изменённые поля требуют новой проверки, а не наследуют прежний статус готовности.</summary>
    private void OnProfileEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loadFailed && StatusBox is not null && sender is TextBox { IsKeyboardFocusWithin: true })
            StatusBox.Text = "Параметры изменены. Сохраните профиль и выполните проверку для этих значений.";
    }

    /// <summary>Сохраняет один PS1 со встроенными профилем и инструментом для выбранной БД.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveForOperation();

            var profile = SelectedProfile();
            var dialog = new SaveFileDialog { Title = "Подготовка PostgreSQL на сервере БД", Filter = "PowerShell script|*.ps1", FileName = "Prepare-PostgreSQL-" + (Profiles.IndexOf(profile) + 1) + ".ps1" };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            _service.Export(profile, dialog.FileName, ((MainWindow)Window.GetWindow(this)).GetPostgreSqlSetupTool());

            StatusBox.Text = "Скрипт сохранён: " + dialog.FileName + "\nСкопируйте его на " + profile.ComputerName + " и запустите в PowerShell. После выполнения скопируйте result.json по пути из консоли. Экспорт не является проверкой готовности.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Импортирует результат автономного скрипта только при совпадении компьютера и всех параметров.</summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveForOperation();

            var dialog = new OpenFileDialog { Title = "Отчёт проверки PostgreSQL", Filter = "JSON report|*.json", CheckFileExists = true };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            StatusBox.Text = Describe(_service.ImportReport(SelectedProfile(), dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Запускает проверку только на текущем компьютере без обращения к Runtime.</summary>
    private async void OnCheckClick(object sender, RoutedEventArgs e) => await RunAsync(false);

    /// <summary>Запрашивает явное подтверждение перезапуска PostgreSQL перед AdoptService.</summary>
    private async void OnPrepareClick(object sender, RoutedEventArgs e) => await RunAsync(true);

    /// <summary>Передаёт локальную операцию главному окну с общей защитой от закрытия и параллельного deployment.</summary>
    private async Task RunAsync(bool prepare)
    {
        try
        {
            SaveForOperation();

            if (Window.GetWindow(this) is not MainWindow window)
                return;

            var report = await window.RunPostgreSqlSetupAsync(SelectedProfile(), prepare);

            if (report is not null)
                StatusBox.Text = Describe(report);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Показывает снимок, дату, фактическое владение службой и путь диагностического журнала.</summary>
    private static string Describe(PostgreSqlSetupReport report) => $"{(report.Ready ? "Проверка пройдена" : "Подготовка не подтверждена")}\nМомент проверки: {report.CheckedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nКомпьютер: {report.Computer}\nСлужба: {report.State?.ServiceName}; состояние: {report.State?.ServiceState}\nManagedByService: {report.State?.ManagedByService}\nService PID: {report.State?.ServiceProcessId}; PostgreSQL PID: {report.State?.PostmasterProcessId}\n{report.Message}\nЖурнал на сервере БД: {report.LogPath}\nЭто результат на указанное время. Перед первоначальной подготовкой локального сервера выполняется свежая проверка.";

    /// <summary>Сохраняет ошибку на странице и показывает её оператору.</summary>
    private void ShowError(Exception error)
    {
        StatusBox.Text = error.Message;
        MessageBox.Show(Window.GetWindow(this), error.Message, "Подготовка PostgreSQL", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}