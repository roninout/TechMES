using System.Windows;
using System.Windows.Controls;
using TechMES.PostgreSql.Control;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSwitchControl : UserControl
{
    private PostgreSqlMaintenanceSnapshot? _snapshot;
    private bool _busy;
    private bool _loaded;

    // Создаёт панель; настройка доверия выполняется один раз штатным инструментом.
    public PostgreSqlSwitchControl() => InitializeComponent();

    // При первом показе читает состояние, не создавая план и не меняя PostgreSQL.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        await InspectAsync();
    }

    // Позволяет явно обновить фактические роли и сохранённую историю.
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await InspectAsync();

    // Свежие роли проверяются повторно перед созданием нового плана.
    private async void OnSwitchClick(object sender, RoutedEventArgs e) => await ExecuteAsync(false);

    // Продолжает прежние идентификаторы; NeedsReview остаётся заблокированным.
    private async void OnResumeClick(object sender, RoutedEventArgs e) => await ExecuteAsync(true);

    // Показывает причину остановки, идентификаторы фаз и пути журналов выбранной операции.
    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryRow row)
            DetailsBox.Text = row.Details;
    }

    // Ошибка обновления убирает возможность запуска по старому снимку.
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
            ConnectionText.Text = "Состояние не получено: " + ex.Message + "\nНастройка доступа: Configure-TechMESPostgreSqlMaintenance.ps1. Файл: " + PostgreSqlMaintenanceConnection.SettingsPath;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Делает только запросы чтения через тот же закреплённый mTLS-клиент.
    private async Task RefreshAsync()
    {
        var connection = PostgreSqlMaintenanceConnection.Load();

        using var certificate = connection.LoadCertificate();
        using var remote = new PostgreSqlSwitchRemote(connection.Network, certificate);

        var session = new PostgreSqlMaintenanceSession(remote, connection.CreateStore(), connection.Network, connection.Control);
        var snapshot = await session.InspectAsync(CancellationToken.None);

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

            var completed = group.Select(item => item.NodeAddress).Distinct().Count() == 2 && group.All(item => item.Completed);
            var state = completed ? "Завершено" : phases.Values.Any(phase => phase.State == "NeedsReview") ? "Требует разбора" : "Не завершено";
            var details = "Plan: " + plan.Id + "\nCluster: " + plan.SystemIdentifier + "\n" + string.Join("\n\n", phases.Values.Select(phase => $"{phase.Mode}: {phase.State}\nOperationId: {phase.Id}\n{phase.Message}\nError: {phase.Error ?? "—"}\nLog: {phase.Log ?? "—"}"));

            return new HistoryRow(plan, plan.CreatedAtUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"), plan.From + " → " + plan.To, state, State("Fence"), State("Promote"), State("Rejoin"), details);
        }).OrderByDescending(row => row.Plan.CreatedAtUtc).ToArray();

        HistoryGrid.ItemsSource = rows;
        HistoryGrid.SelectedItem = snapshot.Pending is { } pending ? rows.FirstOrDefault(row => row.Plan.Id == pending.Id) : rows.FirstOrDefault();

        DetailsBox.Text = (HistoryGrid.SelectedItem as HistoryRow)?.Details ?? "История пуста.";
        ConnectionText.Text = $"Maintenance: {Environment.MachineName}; HTTPS: {connection.Network.NodeAddress}/{connection.Network.PeerAddress}:{connection.Port}; кластер: {connection.SystemIdentifier}\n" + (snapshot.Pending is { } active ? "Незавершённый план: " + active.Id : "Незавершённых планов нет.");
    }

    // Держит общую блокировку Maintenance, а результаты и план сохраняет вне процесса UI.
    private async Task ExecuteAsync(bool resume)
    {
        if (_busy)
            return;

        SetBusy(true);

        try
        {
            await RefreshAsync();

            var snapshot = _snapshot!;
            var pending = snapshot.Pending;
            var from = resume ? pending?.From : snapshot.Nodes.Single(node => node.Local.PostgreSqlRole == "Primary").NodeAddress;
            var to = resume ? pending?.To : snapshot.Nodes.Single(node => node.Local.PostgreSqlRole == "Standby").NodeAddress;

            if (from is null || to is null)
                throw new InvalidOperationException("Незавершённый план не найден.");

            if (resume && snapshot.History.Any(entry => entry.Plan.Id == pending!.Id && entry.Phases.Any(phase => phase.State is "NeedsReview" or "Rejected")))
                throw new InvalidOperationException("Операция требует разбора. Причина и журнал указаны в истории; повторное выполнение заблокировано.");

            if (!resume && pending is not null)
                throw new InvalidOperationException("Сначала продолжите существующую операцию.");

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
                var progress = new Progress<string>(line => ProgressBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine));

                await session.RunAsync(from, to, resume, ((IProgress<string>)progress).Report, CancellationToken.None);
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
                ConnectionText.Text = "Обновление не выполнено: " + ex.Message;
            }

            SetBusy(false);
        }
    }

    // Запрещает параллельный запуск; NeedsReview остаётся доступным только для просмотра.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        SwitchButton.IsEnabled = !busy && _snapshot is { Pending: null } snapshot && snapshot.Nodes.Count(node => node.Local.PostgreSqlRole == "Primary") == 1 && snapshot.Nodes.Count(node => node.Local.PostgreSqlRole == "Standby") == 1;
        ResumeButton.IsEnabled = !busy && _snapshot?.Pending is { } pending && !_snapshot.History.Any(entry => entry.Plan.Id == pending.Id && entry.Phases.Any(phase => phase.State is "NeedsReview" or "Rejected"));
    }

    private sealed record HistoryRow(PostgreSqlSwitchPlan Plan, string Created, string Direction, string State, string Fence, string Promote, string Rejoin, string Details);
}