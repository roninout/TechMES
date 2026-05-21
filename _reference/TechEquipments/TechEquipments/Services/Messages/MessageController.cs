using DevExpress.Xpf.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TechEquipments.ViewModels;

namespace TechEquipments
{
    public sealed class MessageController
    {
        private readonly IMessageService _messageService;
        private readonly MessageViewModel _vm;
        private readonly Dispatcher _dispatcher;
        private readonly Window _ownerWindow;
        private readonly Func<string> _getCurrentUser;

        private CancellationTokenSource? _refreshCts;
        private DispatcherTimer? _viewedTimer;
        private long _pendingViewedMessageId;

        public MessageController(IMessageService messageService, MessageViewModel vm, Dispatcher dispatcher, Window ownerWindow, Func<string> getCurrentUser)
        {
            _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            _getCurrentUser = getCurrentUser ?? throw new ArgumentNullException(nameof(getCurrentUser));
        }

        public async Task InitializeAsync()
        {
            try
            {
                await _messageService.EnsureTableAsync();
                await LoadAsync(silent: true);
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Message init error: {ex.Message}";
            }
        }

        public async Task LoadAsync(bool silent = false, long? keepSelectedId = null)
        {
            if (_vm.IsEditMode)
                return;

            try
            {
                _vm.IsLoading = true;

                var user = GetCurrentUser();

                var messages = await _messageService.GetMessagesAsync(
                    includeInactive: _vm.ShowAllMessages,
                    deviceName: user);

                var activeCount = await _messageService.GetActiveMessageCountAsync();

                ApplyLoadedMessages(messages, activeCount, keepSelectedId);

                if (!silent)
                    _vm.StatusText = $"Messages loaded: {_vm.Messages.Count}";
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Message load error: {ex.Message}";

                if (!silent)
                {
                    DXMessageBox.Show(
                        _ownerWindow,
                        ex.Message,
                        "Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                _vm.IsLoading = false;
            }
        }

        public void StartBackgroundRefresh()
        {
            StopBackgroundRefresh();

            if (!_vm.RefreshEnabled)
                return;

            _refreshCts = new CancellationTokenSource();
            _ = RunBackgroundRefreshAsync(_refreshCts.Token);
        }

        public void StopBackgroundRefresh()
        {
            try
            {
                _refreshCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            _refreshCts?.Dispose();
            _refreshCts = null;

            StopViewedTimer();
        }

        public void AddNew()
        {
            var message = EquipmentMessageDto.CreateNew(GetCurrentUser());

            _vm.Messages.Insert(0, message);
            _vm.SelectedMessage = message;
            _vm.IsEditMode = true;
            _vm.StatusText = "New message created.";

            StopViewedTimer();
        }

        public void BeginEditSelected()
        {
            if (_vm.SelectedMessage == null)
                return;

            _vm.IsEditMode = true;
            _vm.StatusText = "Editing message.";

            StopViewedTimer();
        }

        public async Task SaveSelectedAsync()
        {
            var selected = _vm.SelectedMessage;
            if (selected == null)
                return;

            if (string.IsNullOrWhiteSpace(selected.MessageSubject))
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "Message subject is empty.",
                    "Message",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (string.IsNullOrWhiteSpace(selected.MessageText))
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "Message text is empty.",
                    "Message",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            try
            {
                _vm.IsLoading = true;

                var user = GetCurrentUser();

                var saved = await _messageService.SaveMessageAsync(
                    selected,
                    userName: user,
                    deviceName: user);

                _vm.IsEditMode = false;

                await LoadAsync(silent: true, keepSelectedId: saved.Id);

                _vm.StatusText = "Message saved.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Message save error: {ex.Message}";

                DXMessageBox.Show(
                    _ownerWindow,
                    ex.Message,
                    "Save message",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsLoading = false;
            }
        }

        public async Task DeleteSelectedAsync()
        {
            var selected = _vm.SelectedMessage;
            if (selected == null)
                return;

            if (selected.Id <= 0)
            {
                _vm.Messages.Remove(selected);
                _vm.SelectedMessage = _vm.Messages.FirstOrDefault();
                _vm.IsEditMode = false;
                return;
            }

            var result = DXMessageBox.Show(
                _ownerWindow,
                "Delete selected message?",
                "Delete message",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                _vm.IsLoading = true;

                await _messageService.DeleteMessageAsync(selected.Id);

                _vm.IsEditMode = false;

                await LoadAsync(silent: true);

                _vm.StatusText = "Message deleted.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Delete message error: {ex.Message}";

                DXMessageBox.Show(
                    _ownerWindow,
                    ex.Message,
                    "Delete message",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsLoading = false;
            }
        }

        public async Task ToggleActivitySelectedAsync()
        {
            var selected = _vm.SelectedMessage;
            if (selected == null || selected.Id <= 0)
                return;

            var user = GetCurrentUser();
            var selectedId = selected.Id;

            try
            {
                _vm.IsLoading = true;

                var ok = await _messageService.ToggleActivityAsync(
                    selected.Id,
                    user);

                if (!ok)
                {
                    DXMessageBox.Show(
                        _ownerWindow,
                        "Only the user/device that created this message can change its activity.",
                        "Message activity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    await LoadAsync(silent: true, keepSelectedId: selectedId);
                    return;
                }

                await LoadAsync(silent: true, keepSelectedId: selectedId);

                _vm.StatusText = "Message activity changed.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Activity change error: {ex.Message}";

                DXMessageBox.Show(
                    _ownerWindow,
                    ex.Message,
                    "Message activity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                await LoadAsync(silent: true, keepSelectedId: selectedId);
            }
            finally
            {
                _vm.IsLoading = false;
            }
        }

        public async Task ToggleShowAllAsync()
        {
            _vm.ShowAllMessages = !_vm.ShowAllMessages;
            _vm.RaiseShowButton();

            await LoadAsync(silent: true);

            _vm.StatusText = _vm.ShowAllMessages
                ? "Showing all messages."
                : "Showing active messages only.";
        }

        public void OnSelectedMessageChanged()
        {
            StopViewedTimer();

            var selected = _vm.SelectedMessage;

            if (selected == null)
                return;

            if (_vm.IsEditMode)
                return;

            if (selected.Id <= 0)
                return;

            var user = GetCurrentUser();

            // Автор сообщения НЕ помечается как viewed.
            // Нам нужны только просмотры другими устройствами/пользователями.
            if (string.Equals(selected.CreatedBy, user, StringComparison.OrdinalIgnoreCase))
                return;

            if (selected.IsViewedByCurrentDevice)
                return;

            _pendingViewedMessageId = selected.Id;

            _viewedTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(_vm.MarkAsViewedDelaySeconds)
            };

            _viewedTimer.Tick += async (_, __) =>
            {
                if (_viewedTimer != null)
                {
                    _viewedTimer.Stop();
                    _viewedTimer = null;
                }

                await MarkPendingMessageViewedAsync();
            };

            _viewedTimer.Start();
        }

        private async Task MarkPendingMessageViewedAsync()
        {
            var messageId = _pendingViewedMessageId;
            _pendingViewedMessageId = 0;

            if (messageId <= 0)
                return;

            var selected = _vm.SelectedMessage;

            if (selected == null)
                return;

            if (selected.Id != messageId)
                return;

            if (selected.IsViewedByCurrentDevice)
                return;

            var user = GetCurrentUser();

            // Автор сообщения НЕ пишется в equip_message_view.
            if (string.Equals(selected.CreatedBy, user, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await _messageService.MarkViewedAsync(messageId, user);

                // Это значит: текущий ПК просмотрел чужое сообщение.
                // Галочки в ListBox по этому свойству больше НЕ показываем.
                selected.IsViewedByCurrentDevice = true;

                if (string.IsNullOrWhiteSpace(selected.ViewedByText))
                    selected.ViewedByText = user;
                else if (!selected.ViewedByText
                             .Split(',')
                             .Any(x => string.Equals(x.Trim(), user, StringComparison.OrdinalIgnoreCase)))
                    selected.ViewedByText += ", " + user;

                _vm.StatusText = $"Message viewed by {user}.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = $"Message view status error: {ex.Message}";
            }
        }

        private async Task RunBackgroundRefreshAsync(CancellationToken ct)
        {
            var period = TimeSpan.FromSeconds(Math.Max(5, _vm.RefreshPeriodSeconds));

            using var timer = new PeriodicTimer(period);

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (_vm.IsEditMode)
                        continue;

                    var keepId = await _dispatcher.InvokeAsync(() => _vm.SelectedMessage?.Id);

                    var user = GetCurrentUser();

                    var messages = await _messageService.GetMessagesAsync(
                        includeInactive: _vm.ShowAllMessages,
                        deviceName: user,
                        ct: ct);

                    var activeCount = await _messageService.GetActiveMessageCountAsync(ct);

                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_vm.IsEditMode)
                            return;

                        ApplyLoadedMessages(messages, activeCount, keepId);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    _vm.StatusText = $"Message refresh error: {ex.Message}";
                });
            }
        }

        private void ApplyLoadedMessages(IReadOnlyList<EquipmentMessageDto> messages, int activeCount, long? keepSelectedId)
        {
            var oldSelectedId = keepSelectedId ?? _vm.SelectedMessage?.Id;

            _vm.Messages.Clear();

            foreach (var message in messages)
                _vm.Messages.Add(message);

            _vm.ActiveMessageCount = activeCount;
            _vm.RaiseTabHeader();

            if (oldSelectedId.HasValue)
            {
                _vm.SelectedMessage =
                    _vm.Messages.FirstOrDefault(x => x.Id == oldSelectedId.Value)
                    ?? _vm.Messages.FirstOrDefault();
            }
            else
            {
                _vm.SelectedMessage = _vm.Messages.FirstOrDefault();
            }
        }

        private void StopViewedTimer()
        {
            if (_viewedTimer != null)
            {
                _viewedTimer.Stop();
                _viewedTimer = null;
            }

            _pendingViewedMessageId = 0;
        }

        private string GetCurrentUser()
        {
            var user = (_getCurrentUser() ?? "").Trim();

            return string.IsNullOrWhiteSpace(user)
                ? "Unknown"
                : user;
        }
    }
}