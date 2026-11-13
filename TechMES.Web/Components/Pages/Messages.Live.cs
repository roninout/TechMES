using Radzen;
using TechMES.Contracts.Messages;
using TechMES.Web.Clients;

namespace TechMES.Web.Components.Pages;

/// <summary>
/// Связывает live-соединение с UI страницы и сохраняет незавершённое редактирование.
/// </summary>
public partial class Messages
{
    private bool _disposed;
    private readonly CancellationTokenSource _pageStop = new();
    private string _lastLiveWarning = "";

    /// <summary>
    /// Открывает live-канал для интерактивной страницы.
    /// При предварительном серверном рендере соединение не создаётся.
    /// </summary>
    protected override Task OnAfterRenderAsync(bool firstRender) => firstRender && !_disposed ? StartMessagesHubAsync() : Task.CompletedTask;

    /// <summary>
    /// Передаёт уведомление в renderer-контекст Blazor.
    /// </summary>
    private Task OnLiveMessageAsync(MessageChangedNotification notification) => InvokeAsync(async () =>
    {
        if (_disposed)
            return;

        await OnMessagesChangedAsync(notification);

        if (!_disposed)
            StateHasChanged();
    });

    /// <summary>
    /// После подключения перечитывает снимок.
    /// В редакторе отмечает отложенное обновление вместо изменения текущего ввода.
    /// </summary>
    private Task OnLiveStatusAsync(RuntimeLiveStatus status) => InvokeAsync(async () =>
    {
        if (_disposed)
            return;

        _isRealtimeConnected = status.Connected;
        _lastRealtimeEventText = status.Message;

        if (status.Connected)
        {
            // Убираем только предупреждение live-соединения.
            if (_statusText == _lastLiveWarning)
                ClearStatus();

            _lastLiveWarning = "";

            if (_isEditMode)
                _pendingRealtimeChanges = true;
            else
                await LoadAsync(throwOnFailure: true);
        }
        else
        {
            _lastLiveWarning = status.Message;
            SetStatus(AlertStyle.Warning, status.Message);
        }

        if (!_disposed)
            StateHasChanged();
    });
}