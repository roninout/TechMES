using Radzen;
using TechMES.Contracts.Runtime;
using TechMES.Web.Clients;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Показывает прямые снимки обоих Runtime.
/// Сохраняет существующие периоды опроса активности и health.
/// </summary>
public partial class RuntimeStatusBlock
{
    private readonly CancellationTokenSource _stop = new();
    private readonly PeriodicTimer _activityTimer = new(TimeSpan.FromSeconds(5));
    private readonly PeriodicTimer _healthTimer = new(TimeSpan.FromSeconds(30));

    private RuntimeEndpointStatus[] _states = [];
    private RuntimeEndpointDiagnostics[] _diagnostics = [];
    private RuntimeHealthResponse? _health;
    private DateTimeOffset? _healthReadAt;

    private string _activityError = "";
    private string _healthError = "";

    private bool _isLoading;
    private bool _isHealthLoading;
    private bool _disposed;

    private Task? _activityLoop;
    private Task? _healthLoop;

    /// <summary>
    /// Возвращает свежий снимок единственного готового Runtime.
    /// </summary>
    private RuntimeEndpointStatus? ActiveNode
    {
        get
        {
            var active = _states.Where(IsReady).ToArray();
            return active.Length == 1 ? active[0] : null;
        }
    }

    /// <summary>
    /// Не смешивает health предыдущего процесса с текущим активным Runtime.
    /// </summary>
    private RuntimeHealthResponse? CurrentHealth =>
        ActiveNode?.Activity is { } active
        && _health?.Activity?.InstanceId == active.InstanceId
        && _health.Activity.NodeId == active.NodeId
            ? _health
            : null;

    /// <summary>Цвет общего состояния HTTP-маршрутизации.</summary>
    private string RoutingTone => ActiveNode is not null ? "tone-success" : _states.Count(IsReady) > 1 || _activityError.Length > 0 ? "tone-danger" : "tone-warning";

    /// <summary>Иконка готовности, ожидания или противоречивого состояния.</summary>
    private string RoutingIcon => ActiveNode is not null ? "route" : _states.Count(IsReady) > 1 ? "report_problem" : "sync";

    /// <summary>Показывает адрес готового узла либо причину отсутствия маршрута.</summary>
    private string RoutingText => _activityError.Length > 0 ? "HTTP: ошибка настройки/опроса" : ActiveNode is { } active ? $"HTTP → {active.Endpoint.Authority}" : _states.Count(IsReady) > 1 ? "HTTP заблокирован: два Active" : "HTTP: ожидание Active";

    /// <summary>Время самого раннего ответа в последнем опросе пары.</summary>
    private string CheckedText => _states.Length > 0 ? $"Проверено {_states.Min(state => state.CheckedAtUtc).ToLocalTime():HH:mm:ss}" : "Ожидание данных";

    /// <summary>
    /// Опрос health не задерживает отображение режимов Runtime.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        _healthLoop = RunHealthLoopAsync(_stop.Token);

        await RefreshActivityAsync();

        if (!_disposed)
            _activityLoop = RunActivityLoopAsync(_stop.Token);
    }

    /// <summary>Ручная кнопка обновляет обе части диагностики параллельно.</summary>
    private Task RefreshAsync() => Task.WhenAll(RefreshActivityAsync(), RefreshHealthAsync());

    /// <summary>
    /// Прямой опрос сохраняет сведения о Standby даже при отсутствии Active.
    /// </summary>
    private async Task RefreshActivityAsync()
    {
        if (_disposed || _isLoading)
            return;

        _isLoading = true;

        try
        {
            var states = RuntimeEndpoints.ReadStatusesAsync(_stop.Token);
            var diagnostics = RuntimeEndpoints.ReadDiagnosticsAsync(_stop.Token);

            await Task.WhenAll(states, diagnostics);

            if (_disposed)
                return;

            _states = await states;
            _diagnostics = await diagnostics;
            _activityError = "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _states = [];
            _diagnostics = [];
            _activityError = ex.Message;
        }
        finally
        {
            _isLoading = false;

            if (!_disposed)
                StateHasChanged();
        }
    }

    /// <summary>
    /// Читает существующий health через маршрутизатор готового Runtime.
    /// </summary>
    private async Task RefreshHealthAsync()
    {
        if (_disposed || _isHealthLoading)
            return;

        _isHealthLoading = true;

        try
        {
            var health = await RuntimeStatusApi.GetHealthAsync(_stop.Token);

            if (_disposed)
                return;

            _health = health;
            _healthError = health.Error ?? "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _health = null;
            _healthError = ex.Message;
        }
        finally
        {
            _isHealthLoading = false;
            _healthReadAt = DateTimeOffset.UtcNow;

            if (!_disposed)
                StateHasChanged();
        }
    }

    /// <summary>Обновляет прямые снимки раз в пять секунд.</summary>
    private async Task RunActivityLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _activityTimer.WaitForNextTickAsync(ct))
                await InvokeAsync(RefreshActivityAsync);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Сохраняет прежний тридцатисекундный опрос health.</summary>
    private async Task RunHealthLoopAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                await InvokeAsync(RefreshHealthAsync);
            }
            while (await _healthTimer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Устаревший снимок не отображается как готовый узел.</summary>
    private static bool IsFresh(RuntimeEndpointStatus? state) => state is not null && DateTimeOffset.UtcNow - state.CheckedAtUtc < TimeSpan.FromSeconds(15);

    /// <summary>Готовность узла по свежему снимку.</summary>
    private static bool IsReady(RuntimeEndpointStatus? state) => IsFresh(state) && state!.IsActive;

    /// <summary>Подбирает текст и цвет бейджа по свежему состоянию конкретного Runtime.</summary>
    private (string Text, BadgeStyle Style, string Hint) DescribeRuntime(int index)
    {
        if (index == 1 && _states.Length == 1)
            return ("N/A", BadgeStyle.Base, "Второй отдельный адрес Runtime отсутствует.");

        var state = _states.ElementAtOrDefault(index);

        if (state is null)
            return _activityError.Length > 0
                ? ("Unknown", BadgeStyle.Warning, _activityError)
                : ("Checking", BadgeStyle.Base, "Ожидание данных Runtime.");

        var hint = $"{state.Endpoint.Authority}\n{BuildRuntimeHint(state)}";

        if (!IsFresh(state))
            return ("Stale", BadgeStyle.Warning, hint);

        if (state.Activity is not { } activity)
            return ("Unavailable", BadgeStyle.Danger, hint);

        return activity.Mode switch
        {
            _ when state.IsActive => ("Active", BadgeStyle.Success, hint),
            "Standby" => ("Standby", BadgeStyle.Info, hint),
            "Faulted" => ("Faulted", BadgeStyle.Danger, hint),
            _ => (activity.Mode, BadgeStyle.Warning, hint)
        };
    }

    /// <summary>
    /// Показывает причины, владельца и сроки lease без строк подключения.
    /// </summary>
    private static string BuildRuntimeHint(RuntimeEndpointStatus state)
    {
        var checkedAt = $"Проверено: {state.CheckedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}";

        if (state.Activity is not { } activity)
            return $"{checkedAt}\nСвязь: {state.Error}\nНедоступность HTTP не определяет состояние Windows-службы.";

        var coordination = activity.Coordination;

        return $"Node: {activity.NodeId}\nMode: {activity.Mode}\nКоманды: {(activity.CanExecuteCommands ? "разрешены" : "запрещены")}\nОперации: {activity.PendingOperations}\nПричина: {activity.Reason}\nРежим изменён: {activity.ChangedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n{checkedAt}\nHA: {(coordination?.Enabled == true ? coordination.Role : "Disabled")}\nВладелец: {coordination?.OwnerNodeId ?? "—"}\nLease до: {coordination?.ExpiresAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "—"}\nОшибки координации: {coordination?.ConsecutiveFailures.ToString() ?? "—"}\nКоординация: {coordination?.Message ?? "—"}\nInstance: {activity.InstanceId}\nActivation: {activity.ActivationId ?? "—"}";
    }

    /// <summary>Состояние Calc берётся с соответствующего Runtime, включая резервный.</summary>
    private (string Text, BadgeStyle Style, string Hint) DescribeCalc(int index)
    {
        if (index == 1 && _states.Length == 1)
            return ("N/A", BadgeStyle.Light, "Второй отдельный адрес Runtime отсутствует.");

        var item = _diagnostics.ElementAtOrDefault(index);

        if (!IsFresh(_states.ElementAtOrDefault(index)) || item?.Snapshot?.Calc is not { } calc)
            return ("Unknown", BadgeStyle.Light, $"Runtime {index + 1}: {item?.Error ?? "Ожидание снимка Calc."}");

        var style = calc.Status switch
        {
            "Online" => BadgeStyle.Success,
            "Standby" => BadgeStyle.Info,
            "Offline" => BadgeStyle.Danger,
            "Blocked" or "Waiting" or "Changing" or "Paused" => BadgeStyle.Warning,
            _ => BadgeStyle.Light
        };

        return (calc.Status, style, $"Calc на {item.Endpoint.Authority}: {calc.Status}\nСлужба Windows: {calc.WindowsServiceStatus ?? "—"}\nВладелец lease: {calc.LeaseOwner ?? "—"}\nHeartbeat: {calc.LastHeartbeatAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
    }

    /// <summary>Выбирает один целый снимок, чтобы строки описывали одну и ту же проверку пары БД.</summary>
    private RuntimeEndpointDiagnostics? SelectReplicationSnapshot()
    {
        var now = DateTimeOffset.UtcNow;

        return _diagnostics.Where(item => item.Snapshot?.PostgreSql is not null)
            .OrderByDescending(item => item.Snapshot!.PostgreSql.Enabled)
            .ThenByDescending(item =>
            {
                var cluster = item.Snapshot!.PostgreSql;
                return (IsReplicationFresh(cluster, cluster.Primary, now) ? 1 : 0) + (IsReplicationFresh(cluster, cluster.Secondary, now) ? 1 : 0);
            })
            .ThenByDescending(item => item.Endpoint == ActiveNode?.Endpoint)
            .FirstOrDefault();
    }

    private static bool IsReplicationFresh(PostgreSqlClusterHealthResponse cluster, PostgreSqlServerState node, DateTimeOffset now) =>
        node.Configured && node.CheckedAtUtc is { } at && at <= now.AddSeconds(5) && now - at <= TimeSpan.FromSeconds(Math.Max(5, cluster.HealthPeriodSeconds) * 3d);

    /// <summary>Отдельно показывает фактическую роль, состояние потока и очередь применения WAL.</summary>
    private static (string Text, BadgeStyle Style, string Hint) DescribeReplication(RuntimeEndpointDiagnostics? item, int index)
    {
        if (item?.Snapshot?.PostgreSql is not { } cluster)
            return ("Unknown", BadgeStyle.Light, "Ожидание снимка PostgreSQL.");

        var node = index == 0 ? cluster.Primary : cluster.Secondary;
        var hint = $"{node.Role} DB: {node.ServerAddress ?? "—"} · {node.ActualRole ?? "—"}\nТопология: {cluster.Topology} · проверено {node.CheckedAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n{node.Message}";

        if (!cluster.Enabled || !node.Configured)
            return ("N/A", BadgeStyle.Light, hint);

        if (!IsReplicationFresh(cluster, node, DateTimeOffset.UtcNow))
            return ("Stale", BadgeStyle.Warning, hint);

        if (cluster.Topology is "TwoPrimaries" or "TwoStandbys" or "ClusterMismatch" or "DuplicateEndpoint")
            return ("Conflict", BadgeStyle.Danger, hint);

        if (!node.Connected)
            return ("Offline", BadgeStyle.Danger, hint);

        if (!node.MonitoringOk)
            return ("Unknown", BadgeStyle.Warning, hint);

        if (node.ActualRole == "Primary")
        {
            var lag = node.MaxStreamingReplayLagBytes is { } lagBytes ? $"{lagBytes:N0} B" : "—";
            hint += $"\nFlush LSN: {node.CurrentWalLsn ?? "—"}\nРеплик в streaming: {node.StreamingReplicaCount?.ToString() ?? "—"}\nРежимы sync_state: {node.StreamingSyncStates ?? "—"}\nМакс. отставание replay по streaming-репликам: {lag}";
            return node.StreamingReplicaCount is > 0
                ? (node.StreamingSyncStates ?? "Source", BadgeStyle.Info, hint)
                : ("No replica", BadgeStyle.Warning, hint);
        }

        var backlog = node.ReplayBacklogBytes is { } bytes ? $"{bytes:N0} B" : "—";
        hint += $"\nReceiver: {node.ReceiverStatus ?? "—"} · источник: {node.SenderHost ?? "—"}\nReceive LSN: {node.ReceivedWalLsn ?? "—"} · Replay LSN: {node.ReplayedWalLsn ?? "—"}\nПолучено, но ещё не применено: {backlog}\nПоследнее сообщение: {node.LastMessageReceivedAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm:ss}";

        if (node.ReplayPaused == true)
            return ("Paused", BadgeStyle.Warning, hint);

        return node.ReceiverStatus == "streaming"
            ? ("Streaming", BadgeStyle.Success, hint)
            : ("No stream", BadgeStyle.Warning, hint);
    }

    /// <summary>
    /// При скрытии панели прекращает опрос и освобождает таймеры.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _stop.Cancel();
        _activityTimer.Dispose();
        _healthTimer.Dispose();

        try
        {
            await Task.WhenAll(_activityLoop ?? Task.CompletedTask, _healthLoop ?? Task.CompletedTask);
        }
        finally
        {
            _stop.Dispose();
        }
    }
}