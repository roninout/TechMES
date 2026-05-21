using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace TechEquipments.ViewModels
{
    public sealed class MessageViewModel : ObservableObject
    {
        public ObservableCollection<EquipmentMessageDto> Messages { get; } = new();

        public Array MessageTypeOptions { get; } = Enum.GetValues(typeof(MessageType));

        private EquipmentMessageDto? _selectedMessage;
        public EquipmentMessageDto? SelectedMessage
        {
            get => _selectedMessage;
            set => SetProperty(ref _selectedMessage, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private bool _isEditMode;
        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                if (!SetProperty(ref _isEditMode, value))
                    return;

                Raise(nameof(IsReadOnly));
            }
        }

        public bool IsReadOnly => !IsEditMode;

        private bool _showAllMessages;
        public bool ShowAllMessages
        {
            get => _showAllMessages;
            set
            {
                if (!SetProperty(ref _showAllMessages, value))
                    return;

                Raise(nameof(ShowButtonText));
            }
        }

        public string ShowButtonText => ShowAllMessages ? "Active" : "All";

        private int _activeMessageCount;
        public int ActiveMessageCount
        {
            get => _activeMessageCount;
            set
            {
                if (!SetProperty(ref _activeMessageCount, value))
                    return;

                Raise(nameof(TabHeader));
            }
        }

        public string TabHeader => ActiveMessageCount > 0
            ? $"Message ({ActiveMessageCount})"
            : "Message";

        private bool _showDeleteButton;
        public bool ShowDeleteButton
        {
            get => _showDeleteButton;
            set => SetProperty(ref _showDeleteButton, value);
        }

        private bool _refreshEnabled;
        public bool RefreshEnabled
        {
            get => _refreshEnabled;
            set => SetProperty(ref _refreshEnabled, value);
        }

        private int _refreshPeriodSeconds = 30;
        public int RefreshPeriodSeconds
        {
            get => _refreshPeriodSeconds;
            set => SetProperty(ref _refreshPeriodSeconds, Math.Max(5, value));
        }

        private int _markAsViewedDelaySeconds = 3;
        public int MarkAsViewedDelaySeconds
        {
            get => _markAsViewedDelaySeconds;
            set => SetProperty(ref _markAsViewedDelaySeconds, Math.Max(1, value));
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value ?? "");
        }

        public void RaiseShowButton()
        {
            Raise(nameof(ShowButtonText));
        }

        public void RaiseTabHeader()
        {
            Raise(nameof(TabHeader));
        }

        public bool HasUnsavedMessage =>
            SelectedMessage?.IsNew == true ||
            SelectedMessage?.IsDirty == true ||
            Messages.Any(x => x.IsNew || x.IsDirty);
    }
}