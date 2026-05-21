using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    public sealed class EquipmentInfoNoteDto : INotifyPropertyChanged
    {
        private long _id;
        private string _equipName = "";
        private string _noteText = "";
        private string _createdBy = "";
        private DateTime _createdAt;
        private string? _updatedBy;
        private DateTime? _updatedAt;
        private bool _isDirty;
        private bool _isNew;

        public long Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        public string EquipName
        {
            get => _equipName;
            set => SetField(ref _equipName, value);
        }

        public string NoteText
        {
            get => _noteText;
            set
            {
                if (!SetField(ref _noteText, value))
                    return;

                IsDirty = true;
                OnPropertyChanged(nameof(PreviewText));
            }
        }

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

        public bool IsDirty
        {
            get => _isDirty;
            set => SetField(ref _isDirty, value);
        }

        public bool IsNew
        {
            get => _isNew;
            set => SetField(ref _isNew, value);
        }

        public string CreatedAtText => CreatedAt.ToString("dd.MM.yyyy HH:mm");

        public string UpdatedAtText => UpdatedAt?.ToString("dd.MM.yyyy HH:mm") ?? "";

        public string PreviewText
        {
            get
            {
                var text = (NoteText ?? "").Trim();

                if (string.IsNullOrWhiteSpace(text))
                    return "Empty note";

                text = text.Replace("\r", " ").Replace("\n", " ");

                return text.Length <= 33
                    ? text
                    : text[..33] + "...";
            }
        }

        public void AcceptChanges()
        {
            IsNew = false;
            IsDirty = false;
        }

        public static EquipmentInfoNoteDto CreateNew(string equipName, string createdBy)
        {
            return new EquipmentInfoNoteDto
            {
                Id = 0,
                EquipName = (equipName ?? "").Trim(),
                NoteText = "",
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "Unknown" : createdBy.Trim(),
                CreatedAt = DateTime.Now,
                IsNew = true,
                IsDirty = true
            };
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
    }
}