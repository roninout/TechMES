using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TechMES.PostgreSql.Control;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSwitchControl : UserControl
{
    private readonly DispatcherTimer _refreshTimer;
    private PostgreSqlMaintenanceSnapshot? _snapshot;
    private string _systemIdentifier = "";
    private DateTimeOffset? _completedAtUtc;
    private bool _busy;
    private bool _loaded;

    // Обновляет открытую панель; таймер выполняет только чтение состояния.
    public PostgreSqlSwitchControl()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };

        _refreshTimer.Tick += OnRefreshTimer;
        Unloaded += OnUnloaded;
    }

    // При открытии панели возобновляет чтение и сразу получает состояние.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        _refreshTimer.Start();

        await InspectAsync();
    }

    // Закрытая панель не опрашивает серверы и не отменяет принятые операции.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _refreshTimer.Stop();
    }

    // Во время операции и на скрытой панели автоматические запросы не запускаются.
    private async void OnRefreshTimer(object? sender, EventArgs e)
    {
        if (_loaded && IsVisible && !_busy)
            await InspectAsync();
    }

    // Позволяет явно обновить состояние без запуска переключения.
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await InspectAsync();

    // Перед новым переключением повторно проверяет свежие сведения обоих узлов.
    private async void OnSwitchClick(object sender, RoutedEventArgs e) => await ExecuteAsync(false);

    // Продолжает существующий план с прежними идентификаторами фаз.
    private async void OnResumeClick(object sender, RoutedEventArgs e) => await ExecuteAsync(true);

    // Показывает подробности выбранной операции.
    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryRow row)
            DetailsBox.Text = row.Details;
    }

    // Не допускает параллельных обновлений; при ошибке убирает старые строки узлов.
    private async Task InspectAsync()
    {
        if (_busy)
            return;

        SetBusy(true);

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _snapshot = null;
            NodesGrid.ItemsSource = null;

            ConnectionText.Text = "Состояние не получено: " + ex.Message
                + "\nНастройки доступа: " + PostgreSqlMaintenanceConnection.SettingsPath;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Читает состояние и историю, сохраняя выбранную пользователем запись.
    private async Task RefreshAsync()
    {
        var selectedId = (HistoryGrid.SelectedItem as HistoryRow)?.Plan.Id;
        var connection = PostgreSqlMaintenanceConnection.Load();

        using var certificate = connection.LoadCertificate();
        using var remote = new PostgreSqlSwitchRemote(connection.Network, certificate);

        var session = new PostgreSqlMaintenanceSession(remote, connection.CreateStore(), connection.Network, connection.Control);
        var snapshot = await session.InspectAsync(CancellationToken.None);

        _systemIdentifier = connection.SystemIdentifier;
        _snapshot = snapshot;

        NodesGrid.ItemsSource = snapshot.Nodes.Select(node => new
        {
            Address = node.NodeAddress,
            Role = node.Local.PostgreSqlRole ?? "Unknown",
            Host = node.Local.HostKind,
            Service = node.Local.Service?.ServiceState ?? "Unknown",
            Controller = node.Local.ControllerState,
            Checked = node.Local.CheckedAtUtc?.ToLocalTime().ToString("dd.MM HH:mm:ss") ?? "Нет данных"
        }).ToArray();

        var rows = snapshot.History.GroupBy(item => item.Plan.Id).Select(group =>
        {
            var plan = group.First().Plan;
            var phases = group.SelectMany(item => item.Phases).ToDictionary(item => item.Mode);

            string State(string mode) => phases.TryGetValue(mode, out var phase) ? phase.State : "Missing";

            var completed = group.Select(item => item.NodeAddress).Distinct().Count() == 2
                && group.All(item => item.Completed);

            var state = completed
                ? "Завершено"
                : phases.Values.Any(phase => phase.State == "NeedsReview")
                    ? "Требует разбора"
                    : "Не завершено";

            var details = "Plan: " + plan.Id
                + "\nCluster: " + plan.SystemIdentifier
                + "\n"
                + string.Join("\n\n", phases.Values.Select(phase => $"{phase.Mode}: {phase.State}\nOperationId: {phase.Id}\n{phase.Message}\nError: {phase.Error ?? "—"}\nLog: {phase.Log ?? "—"}"));

            return new HistoryRow(plan, plan.CreatedAtUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"), plan.From + " → " + plan.To, state, State("Fence"), State("Promote"), State("Rejoin"), details);
        }).OrderByDescending(row => row.Plan.CreatedAtUtc).ToArray();

        HistoryGrid.ItemsSource = rows;

        HistoryGrid.SelectedItem = snapshot.Pending is { } pending
            ? rows.FirstOrDefault(row => row.Plan.Id == pending.Id)
            : rows.FirstOrDefault(row => row.Plan.Id == selectedId) ?? rows.FirstOrDefault();

        DetailsBox.Text = (HistoryGrid.SelectedItem as HistoryRow)?.Details ?? "История пуста.";

        var reason = GetStartBlockReason();

        var timelines = string.Join("; ", snapshot.Nodes.Select(node => $"{node.NodeAddress}: {node.Local.Timeline?.ToString() ?? "нет данных"}"));

        ConnectionText.Text =
            $"Maintenance: {Environment.MachineName}; HTTPS: {connection.Network.NodeAddress}/{connection.Network.PeerAddress}:{connection.Port}; кластер: {connection.SystemIdentifier}\n"
            + (snapshot.Pending is { } active
                ? "Незавершённый план: " + active.Id
                : "Незавершённых планов нет.")
            + "\nTimeline контрольных точек: " + timelines
            + "\n" + (reason ?? "Узлы готовы к новому плановому переключению.")
            + "\nАвтообновление открытой панели: каждые 10 секунд.";
    }

    // Разрешает новую операцию только после свежего согласованного состояния пары.
    private string? GetStartBlockReason()
    {
        if (_snapshot is not { } snapshot)
            return "Сначала необходимо получить состояние обоих узлов.";

        if (snapshot.Pending is not null)
            return "Для существующего плана используйте продолжение операции.";

        if (snapshot.Nodes.Length != 2)
            return "Необходимо получить состояние двух узлов.";

        if (_completedAtUtc is { } completed
            && snapshot.Nodes.Any(node => node.Local.CheckedAtUtc is null || node.Local.CheckedAtUtc.Value < completed))
        {
            return "Переключение завершено. Ожидаются снимки обоих контроллеров, полученные после его завершения.";
        }

        var primary = snapshot.Nodes.Where(node => node.Local.PostgreSqlRole == "Primary").ToArray();
        var standby = snapshot.Nodes.Where(node => node.Local.PostgreSqlRole == "Standby").ToArray();

        if (primary.Length != 1 || standby.Length != 1)
        {
            return "Ожидается одна работающая Primary и одна Standby. Текущие роли: "
                + string.Join("; ", snapshot.Nodes.Select(node => node.NodeAddress + "=" + (node.Local.PostgreSqlRole ?? "Unknown")));
        }

        if (primary[0].Local.Timeline is null
            || primary[0].Local.Timeline != standby[0].Local.Timeline)
        {
            return "Timeline контрольных точек пока не согласованы. Дождитесь обновления; если состояние сохраняется, проверьте репликацию.";
        }

        try
        {
            var plan = new PostgreSqlSwitchPlan(new string('0', 32), primary[0].NodeAddress, standby[0].NodeAddress, _systemIdentifier, DateTimeOffset.UtcNow);

            PostgreSqlSwitchCoordinator.CheckInitial(plan, primary[0], standby[0]);

            return null;
        }
        catch (InvalidOperationException ex)
        {
            return "Свежесть состояния, управление службами или готовность команд не подтверждены: "
                + ex.Message;
        }
    }

    // Запускает только явно подтверждённое переключение или продолжение прежнего плана.
    private async Task ExecuteAsync(bool resume)
    {
        if (_busy)
            return;

        SetBusy(true);
        ProgressBox.Clear();

        try
        {
            await RefreshAsync();

            if (!resume && GetStartBlockReason() is { } reason)
                throw new InvalidOperationException(reason);

            var snapshot = _snapshot!;
            var pending = snapshot.Pending;

            var from = resume
                ? pending?.From
                : snapshot.Nodes.Single(node => node.Local.PostgreSqlRole == "Primary").NodeAddress;

            var to = resume
                ? pending?.To
                : snapshot.Nodes.Single(node => node.Local.PostgreSqlRole == "Standby").NodeAddress;

            if (from is null || to is null)
                throw new InvalidOperationException("Незавершённый план не найден.");

            if (resume && snapshot.History.Any(entry => entry.Plan.Id == pending!.Id && entry.Phases.Any(phase => phase.State is "NeedsReview" or "Rejected")))
            {
                throw new InvalidOperationException("Операция требует разбора. Причина и журнал указаны в истории; повторное выполнение заблокировано.");
            }

            if (!resume && MessageBox.Show(Window.GetWindow(this), $"Текущий Primary: {from}\nНовый Primary: {to}\n\nБудут выполнены Fence, Promote и Rejoin. Подключения к прежнему Primary прервутся.\n\nВыполнить плановое переключение?", "Переключение PostgreSQL", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            if (Window.GetWindow(this) is not MainWindow window)
                throw new InvalidOperationException("Главное окно Maintenance недоступно.");

            await window.RunPostgreSqlSwitchAsync(async () =>
            {
                var connection = PostgreSqlMaintenanceConnection.Load();

                using var certificate = connection.LoadCertificate();
                using var remote = new PostgreSqlSwitchRemote(connection.Network, certificate);

                var session = new PostgreSqlMaintenanceSession(remote, connection.CreateStore(), connection.Network, connection.Control);

                var progress = new Progress<string>(line =>
                    ProgressBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine));

                await session.RunAsync(from, to, resume, ((IProgress<string>)progress).Report, CancellationToken.None);

                _completedAtUtc = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception ex)
        {
            ProgressBox.AppendText("Операция не подтверждена: " + ex.Message + Environment.NewLine);
        }
        finally
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _snapshot = null;
                NodesGrid.ItemsSource = null;

                ConnectionText.Text = "Обновление не выполнено: " + ex.Message;
            }

            SetBusy(false);
        }
    }

    // Согласует доступность кнопки с предварительной проверкой координатора.
    private void SetBusy(bool busy)
    {
        _busy = busy;

        var reason = GetStartBlockReason();

        RefreshButton.IsEnabled = !busy;
        SwitchButton.IsEnabled = !busy && reason is null;

        SwitchButton.ToolTip = reason
            ?? "Запустить плановое переключение Primary → Standby.";

        ResumeButton.IsEnabled = !busy
            && _snapshot?.Pending is { } pending
            && !_snapshot.History.Any(entry => entry.Plan.Id == pending.Id && entry.Phases.Any(phase => phase.State is "NeedsReview" or "Rejected"));
    }

    private sealed record HistoryRow(PostgreSqlSwitchPlan Plan, string Created, string Direction, string State, string Fence, string Promote, string Rejoin, string Details);
}