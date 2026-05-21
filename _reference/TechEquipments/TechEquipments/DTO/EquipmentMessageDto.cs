using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    public sealed class EquipmentMessageDto : INotifyPropertyChanged
    {
        private long _id;
        private MessageType _messageType = MessageType.Info;
        private string _messageSubject = "";
        private string _messageText = "";
        private bool _isActive = true;
        private string _createdBy = "";
        private DateTime _createdAt;
        private string? _updatedBy;
        private DateTime? _updatedAt;
        private bool _isViewedByCurrentDevice;
        private bool _isViewedByOtherDevice;
        private string _viewedByText = "";
        private string? _selectedViewedByDevice;
        private bool _isNew;
        private bool _isDirty;

        public long Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        public MessageType MessageType
        {
            get => _messageType;
            set
            {
                if (Equals(_messageType, value))
                    return;

                var oldDefaultSubject = GetDefaultSubject(_messageType);

                // Автоматически меняем Subject только если он ещё пустой
                // или был стандартным значением старого типа.
                var canAutoUpdateSubject =
                    string.IsNullOrWhiteSpace(_messageSubject) ||
                    string.Equals(_messageSubject.Trim(), oldDefaultSubject, StringComparison.OrdinalIgnoreCase);

                _messageType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MessageTypeText));

                IsDirty = true;

                if (canAutoUpdateSubject)
                    MessageSubject = GetDefaultSubject(value);
            }
        }

        public string MessageTypeText => MessageType.ToString();

        public string MessageSubject
        {
            get => _messageSubject;
            set
            {
                if (!SetField(ref _messageSubject, value ?? ""))
                    return;

                IsDirty = true;
            }
        }

        public string MessageText
        {
            get => _messageText;
            set
            {
                if (!SetField(ref _messageText, value ?? ""))
                    return;

                IsDirty = true;
                OnPropertyChanged(nameof(PreviewText));
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (!SetField(ref _isActive, value))
                    return;

                OnPropertyChanged(nameof(ActivityText));
            }
        }

        public string ActivityText => IsActive ? "Active" : "Inactive";

        public string CreatedBy
        {
            get => _createdBy;
            set => SetField(ref _createdBy, value);
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set
            {
                if (!SetField(ref _createdAt, value))
                    return;

                OnPropertyChanged(nameof(CreatedAtText));
            }
        }

        public string CreatedAtText => CreatedAt.ToString("dd.MM.yyyy HH:mm");

        public string? UpdatedBy
        {
            get => _updatedBy;
            set => SetField(ref _updatedBy, value);
        }

        public DateTime? UpdatedAt
        {
            get => _updatedAt;
            set
            {
                if (!SetField(ref _updatedAt, value))
                    return;

                OnPropertyChanged(nameof(UpdatedAtText));
            }
        }

        public string UpdatedAtText => UpdatedAt?.ToString("dd.MM.yyyy HH:mm") ?? "";

        /// <summary>
        /// True только если текущий DeviceName реально есть в equip_message_view.
        /// Автор сообщения сам себе галочки не показывает.
        /// </summary>
        public bool IsViewedByCurrentDevice
        {
            get => _isViewedByCurrentDevice;
            set => SetField(ref _isViewedByCurrentDevice, value);
        }

        /// <summary>
        /// True только для автора сообщения, если это сообщение просмотрело другое устройство.
        /// Используется для ✓✓ в ListBox.
        /// </summary>
        public bool IsViewedByOtherDevice
        {
            get => _isViewedByOtherDevice;
            set => SetField(ref _isViewedByOtherDevice, value);
        }

        /// <summary>
        /// Только устройства/пользователи из equip_message_view.
        /// Автор сообщения сюда не добавляется.
        /// </summary>
        public string ViewedByText
        {
            get => _viewedByText;
            set
            {
                if (!SetField(ref _viewedByText, value ?? ""))
                    return;

                RebuildViewedByDevices();
                OnPropertyChanged(nameof(ViewedByDisplayText));
            }
        }

        /// <summary>
        /// Просмотревшие устройства списком для ComboBox.
        /// Автор сообщения сюда не попадает.
        /// </summary>
        public ObservableCollection<string> ViewedByDevices { get; } = new();

        /// <summary>
        /// Первый элемент для отображения в ComboBox. В список можно раскрыться и увидеть всех.
        /// </summary>
        public string? SelectedViewedByDevice
        {
            get => _selectedViewedByDevice;
            private set => SetField(ref _selectedViewedByDevice, value);
        }

        public string ViewedByDisplayText => ViewedByDevices.Count == 0 ? "—" : string.Join(", ", ViewedByDevices);

        public bool IsNew
        {
            get => _isNew;
            set => SetField(ref _isNew, value);
        }

        public bool IsDirty
        {
            get => _isDirty;
            set => SetField(ref _isDirty, value);
        }

        public string PreviewText
        {
            get
            {
                var text = (MessageText ?? "").Trim();

                if (string.IsNullOrWhiteSpace(text))
                    return "Empty message";

                text = text.Replace("\r", " ").Replace("\n", " ");

                return text.Length <= 30
                    ? text
                    : text[..30] + "...";
            }
        }

        public void AcceptChanges()
        {
            IsNew = false;
            IsDirty = false;
        }

        public static EquipmentMessageDto CreateNew(string createdBy)
        {
            return new EquipmentMessageDto
            {
                Id = 0,
                MessageType = MessageType.Info,
                MessageSubject = GetDefaultSubject(MessageType.Info),
                MessageText = "",
                IsActive = true,
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "Unknown" : createdBy.Trim(),
                CreatedAt = DateTime.Now,

                // Автору галочки не показываем сразу.
                IsViewedByCurrentDevice = false,
                IsViewedByOtherDevice = false,
                ViewedByText = "",

                IsNew = true,
                IsDirty = true
            };
        }

        private void RebuildViewedByDevices()
        {
            ViewedByDevices.Clear();

            var devices = (_viewedByText ?? "")
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var device in devices)
                ViewedByDevices.Add(device);

            SelectedViewedByDevice = ViewedByDevices.FirstOrDefault();

            OnPropertyChanged(nameof(ViewedByDevices));
            OnPropertyChanged(nameof(SelectedViewedByDevice));
            OnPropertyChanged(nameof(ViewedByDisplayText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private static string GetDefaultSubject(MessageType type)
        {
            return type switch
            {
                MessageType.Warning => "Warning",
                MessageType.Critical => "Critical",
                _ => "Info"
            };
        }
    }
}