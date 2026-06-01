using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TechMES.Application.Messages;
using TechMES.Contracts.Messages;
using TechMES.Runtime.Service.Hubs;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Фоновый watcher состояния Messages-хранилища.
///
/// Зачем он нужен:
/// SignalR-события после Save/Edit/Delete Runtime.Service отправляет сразу.
/// Но если таблицы изменились напрямую в PostgreSQL или через другое приложение,
/// Runtime.Service не узнает об этом автоматически.
///
/// Поэтому watcher периодически читает лёгкий snapshot БД.
/// Если snapshot изменился — отправляет SignalR-событие WEB-клиентам.
/// </summary>
public sealed class MessageStorageWatcher : BackgroundService
{
    /// <summary>
    /// Scope factory нужен, чтобы каждый цикл watcher-а получал свежий scoped IMessageStore.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// SignalR hub для отправки события MessagesChanged всем WEB-клиентам.
    /// </summary>
    private readonly IHubContext<MessagesHub> _hubContext;

    /// <summary>
    /// Настройки периода и включения watcher-а.
    /// </summary>
    private readonly IOptions<MessagesOptions> _options;

    /// <summary>
    /// Runtime-контекст нужен, чтобы подписать событие именем текущего сервиса/устройства.
    /// </summary>
    private readonly IAppRuntimeContext _runtime;

    /// <summary>
    /// Логгер фоновой проверки Messages-хранилища.
    /// </summary>
    private readonly ILogger<MessageStorageWatcher> _logger;

    /// <summary>
    /// Последний снимок хранилища для сравнения с новым циклом.
    /// </summary>
    private MessageStorageSnapshot? _lastSnapshot;

    /// <summary>
    /// Создает watcher внешних изменений Messages-хранилища.
    /// </summary>
    public MessageStorageWatcher(
        IServiceScopeFactory scopeFactory,
        IHubContext<MessagesHub> hubContext,
        IOptions<MessagesOptions> options,
        IAppRuntimeContext runtime,
        ILogger<MessageStorageWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Основной цикл: периодически читает snapshot и отправляет SignalR-событие при изменении.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.EnableStorageWatcher)
        {
            _logger.LogInformation("MessageStorageWatcher отключён настройкой Messages:EnableStorageWatcher.");
            return;
        }

        var seconds = Math.Max(5, options.RefreshPeriodSeconds);
        var period = TimeSpan.FromSeconds(seconds);

        _logger.LogInformation(
            "MessageStorageWatcher запущен. Период проверки: {Seconds} сек.",
            seconds);

        // Первый snapshot просто запоминаем.
        // На старте сервиса не нужно сразу отправлять WEB-клиентам событие об изменении.
        _lastSnapshot = await TryReadSnapshotAsync(stoppingToken);

        using var timer = new PeriodicTimer(period);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var currentSnapshot = await TryReadSnapshotAsync(stoppingToken);

            if (currentSnapshot is null)
                continue;

            if (_lastSnapshot is null)
            {
                _lastSnapshot = currentSnapshot;
                continue;
            }

            if (currentSnapshot == _lastSnapshot)
                continue;

            _logger.LogInformation(
                "MessageStorageWatcher обнаружил изменение Messages-хранилища. " +
                "Отправляем SignalR-событие.");

            _lastSnapshot = currentSnapshot;

            await NotifyStorageChangedAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Безопасно читает snapshot из текущего IMessageStore.
    /// </summary>
    private async Task<MessageStorageSnapshot?> TryReadSnapshotAsync(
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();

            return await messageStore.GetStorageSnapshotAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Если БД временно недоступна — не падаем всем сервисом.
            // Просто пишем warning и попробуем снова на следующем цикле.
            _logger.LogWarning(
                ex,
                "Не удалось прочитать snapshot Messages-хранилища.");

            return null;
        }
    }

    /// <summary>
    /// Уведомляет все WEB-клиенты, что Messages-хранилище изменилось вне текущего HTTP-запроса.
    /// </summary>
    private Task NotifyStorageChangedAsync(CancellationToken ct)
    {
        var notification = new MessageChangedNotification
        {
            EventType = MessageChangedEventType.StorageChanged,
            MessageId = null,
            ChangedBy = _runtime.DeviceName,
            ChangedAt = DateTime.Now
        };

        return _hubContext.Clients.All.SendAsync(
            "MessagesChanged",
            notification,
            ct);
    }
}
