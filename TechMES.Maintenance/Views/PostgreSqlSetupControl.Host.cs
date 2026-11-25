using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TechMES.Contracts.Runtime;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;
using TechMES.PostgreSql.Control;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextBox = System.Windows.Controls.TextBox;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    public sealed class ServerCard(PostgreSqlSetupProfile profile) : INotifyPropertyChanged
    {
        public PostgreSqlSetupProfile Profile { get; } = profile;
        public string Address => Profile.ComputerName;

        public string Summary => Live is null && Report is { Ready: false }
            ? "Готовность не подтверждена"
            : DescribeStatus(Live ?? Report?.Local);

        public string Source => Live is { } live
            ? $"По сети · {FormatTime(live.CheckedAtUtc)}"
            : Report is { } report
                ? $"Отчёт от {FormatTime(report.CheckedAtUtc)}"
                : NetworkError.Length > 0 ? "Снимок недоступен" : "Ожидает проверки";

        internal PostgreSqlRemoteStatus? LiveNode { get; private set; }
        internal PostgreSqlLocalHealthResponse? Live => LiveNode?.Local;
        internal PostgreSqlHostSetupReport? Report { get; private set; }
        internal string NetworkError { get; private set; } = "";
        internal string BundleDirectory { get; set; } = "";
        internal string BundleInstructions { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetNetworkStatus(PostgreSqlRemoteStatus? status, string error = "")
        {
            LiveNode = status;
            NetworkError = error;
            Notify();
        }

        internal void SetReport(PostgreSqlHostSetupReport report)
        {
            Report = report;

            if (Live?.CheckedAtUtc is { } time && time <= report.CheckedAtUtc)
                LiveNode = null;

            Notify();
        }

        internal void Clear()
        {
            LiveNode = null;
            Report = null;
            NetworkError = "";
            BundleDirectory = "";
            BundleInstructions = "";
            Notify();
        }

        private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private enum ControllerAction
    {
        Check,
        Configure,
        InstallAgent
    }

    private async void OnRefreshCardsClick(object sender, RoutedEventArgs e) => await RefreshCardsAsync();

    /// <summary>Читает состояние каждого узла тем же mTLS-клиентом, что и панель переключения.</summary>
    private async Task RefreshCardsAsync(bool manageActions = true)
    {
        if (manageActions)
        {
            _wizardPairVerified = false;
            SetCardActionsEnabled(false);
        }
        ActionStatus.Text = "Читаем состояние контроллеров…";

        try
        {
            var connection = PostgreSqlMaintenanceConnection.Load();
            using var certificate = connection.LoadCertificate();
            var network = connection.Network;
            using var remote = new PostgreSqlSwitchRemote(network, certificate);

            var nodes = await Task.WhenAll(ReadNodeAsync(network.NodeAddress), ReadNodeAsync(network.PeerAddress));

            foreach (var card in Cards)
            {
                var matching = nodes.Where(node =>
                    string.Equals(node.Address, card.Address, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Status?.Local.Service?.Computer, card.Address, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (matching.Length == 1)
                {
                    card.SetNetworkStatus(matching[0].Status, matching[0].Error);
                }
                else
                {
                    card.SetNetworkStatus(null, matching.Length == 0
                        ? "Нет ответа для этого профиля. Проверьте адрес или загрузите отчёт с сервера."
                        : "Профиль совпал с несколькими узлами. Укажите IPv4 сервера.");
                }
            }

            async Task<(string Address, PostgreSqlRemoteStatus? Status, string Error)> ReadNodeAsync(string address)
            {
                try
                {
                    var status = await remote.StatusAsync(address, CancellationToken.None);

                    if (!string.Equals(status.NodeAddress, address, StringComparison.Ordinal))
                        return (address, null, "Сервер вернул состояние другого узла.");

                    var now = DateTimeOffset.UtcNow;
                    if (!PostgreSqlWizardDiagnostics.IsFresh(status.Local.CheckedAtUtc, status.Local.HealthPeriodSeconds, now))
                        return (address, null, $"Снимок контроллера устарел · {FormatTime(status.Local.CheckedAtUtc)}");

                    return (address, status, "");
                }
                catch (Exception ex)
                {
                    return (address, null, $"Состояние {address} недоступно. {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            foreach (var card in Cards)
                card.SetNetworkStatus(null, "Клиент mTLS для PostgreSQL ещё не настроен на этом компьютере. "
                    + "В мастере HA пройдите шаги A–D блока «Сетевая связь и сертификаты». Шаг C создаёт файл client.json.");
        }
        catch (Exception ex)
        {
            foreach (var card in Cards)
                card.SetNetworkStatus(null, "Не удалось подключиться через mTLS. " + ex.Message);
        }
        finally
        {
            ActionStatus.Text = Cards.Any(card => card.NetworkError.Length > 0)
                ? "Есть недоступные данные. Причина в подробностях выбранного сервера."
                : "";

            UpdateSelectedCard();
            if (manageActions)
                SetCardActionsEnabled(true);
        }
    }

    private async void OnCheckHostClick(object sender, RoutedEventArgs e) => await RunControllerActionAsync(ControllerAction.Check);

    private async void OnPrepareHostClick(object sender, RoutedEventArgs e) => await RunControllerActionAsync(ControllerAction.Configure);

    private async void OnInstallAgentClick(object sender, RoutedEventArgs e) => await RunControllerActionAsync(ControllerAction.InstallAgent);

    /// <summary>Выбирает локальный запуск или экспорт для того же действия над выбранным сервером.</summary>
    private async Task RunControllerActionAsync(ControllerAction action, PostgreSqlFailbackSetup? failback = null)
    {
        SetCardActionsEnabled(false);

        try
        {
            var card = SelectedCard();
            var profile = card.Profile;
            var install = action == ControllerAction.InstallAgent;
            var prepare = action != ControllerAction.Check;
            var local = PostgreSqlSetupService.IsLocal(profile);
            if (prepare)
                _wizardPairVerified = false;

            if (install && (EffectiveHost(card) != "Standalone" || string.IsNullOrWhiteSpace(profile.AgentPackageDirectory)))
                throw new InvalidOperationException("Для установки нужен выбранный сервер с отдельным Agent и готовый пакет.");

            if (Window.GetWindow(this) is not MainWindow window)
                return;

            if (local && prepare)
            {
                var service = install ? "TechMES.PostgreSql.Agent" : ControllerService(EffectiveHost(card));

                var operation = failback is null ? "При подготовке" : "При изменении настройки автовозврата";
                if (MessageBox.Show(window, $"Компьютер: {profile.ComputerName}\n{operation} будет перезапущена служба {service}. Продолжить?", "Подготовка контроллера", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                    return;
            }

            string? outputDirectory = null;

            if (!local)
            {
                var dialog = new OpenFolderDialog { Title = $"Папка комплекта для {profile.ComputerName}" };

                if (dialog.ShowDialog(window) != true)
                    return;

                outputDirectory = dialog.FolderName;
            }

            if (install)
            {
                profile.HostMode = "Standalone";
                HostModeField.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
            }

            SaveForOperation();

            if (local)
            {
                var report = await window.RunPostgreSqlHostSetupAsync(profile, prepare, includePackage: install, failback: failback);
                card.SetReport(report);
                ActionStatus.Text = failback is null ? ReportOutcome(report) : report.Message;
            }
            else
            {
                var mode = failback is not null ? "Failback" : prepare ? "Prepare" : "Check";
                var directory = await window.ExportPostgreSqlHostSetupAsync(profile, outputDirectory!, includePackage: install, failback: failback);

                card.BundleDirectory = directory;
                card.BundleInstructions = $"{directory}\nСкопируйте всю папку на {profile.ComputerName}. Запустите команду из неё в PowerShell от администратора.\n\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\\Prepare-Controller.ps1' -Mode {mode} -NoPause\n\nВерните файл result.json по пути из строки Result: и нажмите «Загрузить результат…».";

                ActionStatus.Text = failback is not null ? "Комплект настройки автовозврата сохранён." : prepare ? "Комплект подготовки сохранён." : "Комплект проверки сохранён.";
            }
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
        }
        finally
        {
            UpdateSelectedCard();
            SetCardActionsEnabled(true);
        }
    }

    private void OnChooseAgentPackageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Распакованный пакет Agent с manifest.json" };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SetAgentPackage(dialog.FolderName);
    }

    private async void OnBuildAgentClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window)
            return;

        var dialog = new OpenFolderDialog { Title = "Папка для сборки PostgreSQL Agent" };

        if (dialog.ShowDialog(window) != true)
            return;

        SetCardActionsEnabled(false);
        ActionStatus.Text = "Собираем Agent…";

        try
        {
            SetAgentPackage(await window.BuildPostgreSqlAgentAsync(dialog.FolderName));
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
        }
        finally
        {
            SetCardActionsEnabled(true);
        }
    }

    private void SetAgentPackage(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "manifest.json")) || !Directory.Exists(Path.Combine(directory, "PostgreSql.Agent")))
        {
            ActionStatus.Text = "Выберите папку с manifest.json и подкаталогом PostgreSql.Agent.";
            return;
        }

        var card = SelectedCard();
        card.Profile.AgentPackageDirectory = directory;
        card.BundleDirectory = "";
        card.BundleInstructions = "";

        AgentPackagePath.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        UpdateSelectedCard();
        ActionStatus.Text = "Пакет Agent выбран. Можно запускать установку или экспортировать комплект.";
    }

    private void OnOpenHostFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SelectedCard().BundleDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
        }
    }

    private void OnImportHostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Title = "Отчёт контроллера PostgreSQL", Filter = "JSON report|*.json", CheckFileExists = true };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            var card = SelectedCard();
            var report = new PostgreSqlHostSetupService().ReadReport(card.Profile, dialog.FileName);

            if (card.Report is { } previous && previous.CheckedAtUtc > report.CheckedAtUtc)
                throw new InvalidDataException("Для этого сервера уже загружен более свежий отчёт.");

            card.SetReport(report);
            ActionStatus.Text = ReportOutcome(report);
            UpdateSelectedCard();
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
        }
    }

    private void SetCardActionsEnabled(bool enabled)
    {
        ServerCards.IsEnabled = enabled;
        ActionsPanel.IsEnabled = enabled;
    }

    private void UpdateSelectedCard()
    {
        if (ServerCards.SelectedItem is not ServerCard card)
            return;

        var local = PostgreSqlSetupService.IsLocal(card.Profile);
        var kind = EffectiveHost(card);

        TargetText.Text = $"{card.Address} · {ControllerName(kind)}";

        PreparationHint.Text = kind == "Auto"
            ? "Тип контроллера будет определён на сервере. Для установки Agent выберите Standalone в настройках профиля."
            : $"При подготовке будет перезапущена служба {ControllerService(kind)}.";

        CheckHostButton.Content = local ? "Проверить контроллер" : "Экспорт проверки…";

        PrepareHostButton.Content = (local, kind) switch
        {
            (true, "Runtime") => "Настроить контроллер Runtime",
            (true, "Standalone") => "Настроить установленный Agent",
            (true, _) => "Настроить контроллер",
            (false, "Runtime") => "Экспорт настройки Runtime…",
            (false, "Standalone") => "Экспорт настройки Agent…",
            _ => "Экспорт подготовки…"
        };

        CheckDatabaseButton.IsEnabled = local;
        AdoptDatabaseButton.IsEnabled = local;

        AgentSection.Visibility = kind == "Standalone" ? Visibility.Visible : Visibility.Collapsed;
        AgentModeHint.Visibility = card.Profile.HostMode == "Auto" ? Visibility.Visible : Visibility.Collapsed;
        BuildAgentButton.IsEnabled = Window.GetWindow(this) is MainWindow { CanBuildPostgreSqlAgent: true };
        InstallAgentButton.Content = local ? "Установить или обновить Agent" : "Экспорт установки Agent…";
        InstallAgentButton.IsEnabled = !string.IsNullOrWhiteSpace(card.Profile.AgentPackageDirectory);

        TransferPanel.Visibility = card.BundleDirectory.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferInstructions.Text = card.BundleInstructions;
        HostDetails.Text = DescribeDetails(card);
        WizardTarget.Text = $"Выбранный узел: {card.Address} · {ControllerName(kind)}";
        WizardTransferText.Text = card.BundleInstructions;
        WizardTransferPanel.Visibility = TransferPanel.Visibility;
        UpdateWizardPolicyButton();
    }

    private PostgreSqlSwitchControl? _switchControl;

    /// <summary>Обе вкладки используют одну панель и один экземпляр состояния переключения.</summary>
    private void OnActionTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ActionsPanel) || SwitchContent is null || HistoryContent is null)
            return;

        var history = ReferenceEquals(ActionsPanel.SelectedItem, HistoryTab);
        var host = history ? HistoryContent : ReferenceEquals(ActionsPanel.SelectedItem, SwitchingTab) ? SwitchContent : null;

        if (host is null)
            return;

        _switchControl ??= new PostgreSqlSwitchControl();
        _switchControl.ShowHistory(history);

        if (ReferenceEquals(host.Content, _switchControl))
            return;

        SwitchContent.Content = null;
        HistoryContent.Content = null;
        host.Content = _switchControl;
    }

    private static string EffectiveHost(ServerCard card)
    {
        if (card.Profile.HostMode != "Auto")
            return card.Profile.HostMode;

        var kind = card.Live?.HostKind ?? card.Report?.HostKind;
        return kind is "Runtime" or "Standalone" ? kind : "Auto";
    }

    private static string ControllerName(string kind) => kind switch
    {
        "Standalone" => "Agent",
        "Runtime" => "Runtime",
        _ => "Автовыбор"
    };

    private static string ControllerService(string kind) => kind switch
    {
        "Standalone" => "TechMES.PostgreSql.Agent",
        "Runtime" => "TechMES.Runtime.Service",
        _ => "Runtime или Agent (по результату проверки на сервере)"
    };

    private static string FormatTime(DateTimeOffset? time) => time?.ToLocalTime().ToString("dd.MM HH:mm:ss") ?? "время неизвестно";

    private static string DescribeStatus(PostgreSqlLocalHealthResponse? status) => status is null
        ? "Роль и состояние неизвестны"
        : $"{status.PostgreSqlRole ?? "Роль неизвестна"} · {ControllerName(status.HostKind)} · {status.ControllerState}";

    private static string ReportOutcome(PostgreSqlHostSetupReport report) => report.Ready
        ? report.PreparationCompleted ? "Подготовка завершена. Контроллер готов." : "Проверка пройдена. Контроллер готов."
        : "Готовность контроллера не подтверждена. Причина в подробностях.";

    private static string DescribeDetails(ServerCard card)
    {
        var lines = new List<string>();

        if (card.NetworkError.Length > 0)
            lines.Add(card.NetworkError);

        if (card.Live is { } live)
            lines.Add($"По сети · {FormatTime(live.CheckedAtUtc)}\n{DescribeStatus(live)}\nСлужба БД: {live.Service?.ServiceState ?? "неизвестно"}\nВладение контроллером: {live.OwnsController}\n{live.Message}");

        if (card.Report is { } report)
            lines.Add($"Отчёт от {FormatTime(report.CheckedAtUtc)}\n{ReportOutcome(report)}\nКомпьютер: {report.Computer}\nКонтроллер: {DescribeStatus(report.Local)}\nСлужба: {report.HostService} · {report.HostServiceState} · PID {report.HostProcessId}\nНастройки: {report.SettingsPath}\n{report.Message}\nЖурнал: {report.LogPath}");

        if ((card.Live ?? card.Report?.Local)?.Service is { PostmasterProcessId: > 0, ManagedByService: false })
            lines.Add("Работающий PostgreSQL не принадлежит указанной Windows-службе. Проверьте состояние службы на вкладке Service.");

        return lines.Count > 0 ? string.Join("\n\n", lines) : "Обновите состояние или загрузите отчёт с сервера.";
    }
}
