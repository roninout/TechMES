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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MessagesHub> _hubContext;
    private readonly IOptions<MessagesOptions> _options;
    private readonly IAppRuntimeContext _runtime;
    private readonly ILogger<MessageStorageWatcher> _logger;

    private MessageStorageSnapshot? _lastSnapshot;

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