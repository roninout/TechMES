using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Messages;
using TechMES.Web.Settings;

namespace TechMES.Web.Clients;

/// <summary>
/// Состояние live-соединения страницы с выбранным Runtime.
/// </summary>
public sealed record RuntimeLiveStatus(bool Connected, Uri? Endpoint, string Message);

/// <summary>
/// Поддерживает одну подписку Messages на активный Runtime.
/// Проверяет роль даже при живом соединении: бывший Active может продолжать работать в Standby.
/// Экземпляр принадлежит одной странице и освобождается при уходе с неё.
/// </summary>
public sealed class RuntimeMessagesConnection(RuntimeEndpointSelector selector, IOptions<RuntimeServiceOptions> options, ILogger<RuntimeMessagesConnection> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private Func<MessageChangedNotification, Task>? _changed;
    private Func<RuntimeLiveStatus, Task>? _statusChanged;

    private volatile HubConnection? _connection;
    private Uri? _endpoint;
    private RuntimeLiveStatus? _lastStatus;
    private Task? _loop;

    private int _started;
    private int _disposed;

    /// <summary>
    /// Запускает наблюдение без блокировки загрузки страницы.
    /// Повторный запуск одного экземпляра запрещён.
    /// </summary>
    public void Start(Func<MessageChangedNotification, Task> changed, Func<RuntimeLiveStatus, Task> statusChanged)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Messages live connection has already been started.");

        _changed = changed;
        _statusChanged = statusChanged;
        _loop = RunAsync();
    }

    /// <summary>
    /// Повторяет выбор адреса после ошибок запуска, обрывов и смены роли Runtime.
    /// </summary>
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await SynchronizeAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await ReleaseConnectionAsync().ConfigureAwait(false);

                    try
                    {
                        await ReportAsync(new(false, null, "Live updates are reconnecting: " + ex.Message)).ConfigureAwait(false);
                    }
                    catch (Exception callbackError)
                    {
                        logger.LogDebug(callbackError, "Messages live status callback failed.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            await ReleaseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Сохраняет подписку, пока тот же Runtime остаётся единственным Active.
    /// После смены адреса создаёт новое соединение и подписку.
    /// </summary>
    private async Task SynchronizeAsync(CancellationToken ct)
    {
        var endpoint = await selector.SelectAsync(ct).ConfigureAwait(false);

        if (_endpoint == endpoint && _connection?.State == HubConnectionState.Connected)
            return;

        await ReleaseConnectionAsync().ConfigureAwait(false);
        await ReportAsync(new(false, endpoint, "Connecting live updates...")).ConfigureAwait(false);

        var path = options.Value.MessagesHubPath.TrimStart('/');

        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Relative, out _))
            throw new InvalidOperationException("MessagesHubPath must be a non-empty relative path.");

        var hubUri = new Uri(endpoint, path);
        var connection = new HubConnectionBuilder().WithUrl(hubUri).Build();

        _connection = connection;
        _endpoint = endpoint;

        // Каждое новое соединение получает собственную подписку.
        // Уведомления уже отозванного соединения игнорируются.
        connection.On<MessageChangedNotification>("MessagesChanged", notification => ReceiveAsync(connection, notification));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await connection.StartAsync(timeout.Token).ConfigureAwait(false);

        // За время подключения роль могла измениться.
        if (await selector.SelectAsync(ct).ConfigureAwait(false) != endpoint)
            throw new HttpRequestException("Active Runtime changed while the live connection was starting.");

        // Страница перечитывает HTTP-снимок. Ошибка запускает повторную синхронизацию.
        await ReportAsync(new(true, endpoint, $"Live connected: {hubUri}")).ConfigureAwait(false);
    }

    /// <summary>
    /// Передаёт уведомление текущего соединения и наблюдает ошибки обработчика.
    /// </summary>
    private async Task ReceiveAsync(HubConnection source, MessageChangedNotification notification)
    {
        if (_stop.IsCancellationRequested || !ReferenceEquals(source, _connection))
            return;

        try
        {
            await _changed!(notification).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Messages live notification callback failed.");
        }
    }

    /// <summary>
    /// Передаёт изменившееся состояние странице без повторения одинаковых уведомлений.
    /// </summary>
    private async Task ReportAsync(RuntimeLiveStatus status)
    {
        if (_stop.IsCancellationRequested || status == _lastStatus)
            return;

        _lastStatus = status;

        await _statusChanged!(status).ConfigureAwait(false);
    }

    /// <summary>
    /// Сначала отзывает старое соединение, затем освобождает транспорт и подписки.
    /// </summary>
    private async Task ReleaseConnectionAsync()
    {
        var old = _connection;

        _connection = null;
        _endpoint = null;

        if (old is null)
            return;

        try
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing the previous Messages live connection failed.");
        }
    }

    /// <summary>
    /// Останавливает наблюдение и закрывает соединение.
    /// Повторное освобождение допустимо.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();

        if (_loop is not null)
            await _loop.ConfigureAwait(false);

        _stop.Dispose();
    }
}