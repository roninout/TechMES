using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TechMES.Application.Messages;
using TechMES.Contracts.Messages;
using TechMES.Runtime.Service.Hubs;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Проверяет внешние изменения Messages только в активном Runtime.
/// </summary>
public sealed class MessageStorageWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MessagesHub> _hubContext;
    private readonly IOptions<MessagesOptions> _options;
    private readonly IAppRuntimeContext _runtime;
    private readonly ILogger<MessageStorageWatcher> _logger;
    private readonly RuntimeActivityState _activity;

    private string? _activationId;
    private MessageStorageSnapshot? _lastSnapshot;

    public MessageStorageWatcher(IServiceScopeFactory scopeFactory, IHubContext<MessagesHub> hubContext, IOptions<MessagesOptions> options, IAppRuntimeContext runtime, ILogger<MessageStorageWatcher> logger, RuntimeActivityState activity)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _options = options;
        _runtime = runtime;
        _logger = logger;
        _activity = activity;
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

        _logger.LogInformation("MessageStorageWatcher запущен. Период проверки: {Seconds} сек.", seconds);

        using var timer = new PeriodicTimer(period);

        do
        {
            var state = _activity.GetStatus();

            if (!state.CanExecuteCommands)
            {
                _lastSnapshot = null;
                _activationId = null;
                continue;
            }

            try
            {
                using var lease = _activity.Acquire();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.CancellationToken);

                var currentSnapshot = await TryReadSnapshotAsync(stop.Token);

                if (currentSnapshot is null)
                    continue;

                // После смены эпохи старый baseline не используется.
                if (_lastSnapshot is null || _activationId != lease.ActivationId)
                {
                    _activationId = lease.ActivationId;
                    _lastSnapshot = currentSnapshot;
                    continue;
                }

                if (currentSnapshot == _lastSnapshot)
                    continue;

                _logger.LogInformation("MessageStorageWatcher обнаружил изменение Messages-хранилища. Отправляем SignalR-событие.");

                _lastSnapshot = currentSnapshot;

                await NotifyStorageChangedAsync(stop.Token);
            }
            catch (RuntimeNotActiveException)
            {
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<MessageStorageSnapshot?> TryReadSnapshotAsync(CancellationToken ct)
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
            _logger.LogWarning(ex, "Не удалось прочитать snapshot Messages-хранилища.");

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

        return _hubContext.Clients.All.SendAsync("MessagesChanged", notification, ct);
    }
}