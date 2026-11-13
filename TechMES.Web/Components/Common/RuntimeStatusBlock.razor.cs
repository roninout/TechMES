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
            var states = await RuntimeEndpoints.ReadStatusesAsync(_stop.Token);

            if (_disposed)
                return;

            _states = states;
            _activityError = "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _states = [];
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

    /// <summary>HTTP-недоступность отличается от штатного Standby.</summary>
    private string NodeText(RuntimeEndpointStatus? state) => state is null ? (_activityError.Length > 0 ? "Unknown" : "Checking") : !IsFresh(state) ? "Stale" : state.Activity is null ? "Unavailable" : state.Activity.Mode;

    /// <summary>Цвет фактического состояния узла.</summary>
    private static string NodeTone(RuntimeEndpointStatus? state) => !IsFresh(state) ? "tone-muted" : state!.Activity is null || state.Activity.Mode == "Faulted" ? "tone-danger" : state.IsActive ? "tone-success" : state.Activity.Mode == "Standby" ? "tone-info" : "tone-warning";

    /// <summary>Иконка состояния из набора Material Symbols, используемого темой WEB.</summary>
    private static string NodeIcon(RuntimeEndpointStatus? state) => !IsFresh(state) ? "schedule" : state!.Activity is null ? "cloud_off" : state.IsActive ? "check_circle" : state.Activity.Mode == "Standby" ? "pause_circle" : state.Activity.Mode == "Faulted" ? "error" : "sync";

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