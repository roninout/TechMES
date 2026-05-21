using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Один библиотечный файл вкладки Info.
    /// Может быть связан с несколькими equipment.
    /// </summary>
    public sealed class EquipmentInfoFileDto : INotifyPropertyChanged
    {
        private long _id;
        private string _equipName = "";
        private string _equipTypeGroupKey = "";
        private string _fileName = "";
        private string _displayName = "";
        private string _fileHash = "";
        private byte[]? _fileData;
        private int _sortOrder;
        private DateTime? _updatedAt;
        private bool _isLinkedToCurrentEquipment;

        public long Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        /// <summary>
        /// Для linked-коллекций текущего equipment.
        /// Для library-коллекций может быть пустым.
        /// </summary>
        public string EquipName
        {
            get => _equipName;
            set => SetField(ref _equipName, value);
        }

        /// <summary>
        /// Ключ группы типа оборудования, например:
        /// Motor, AI, DI, VGA, VGD, Atv...
        /// </summary>
        public string EquipTypeGroupKey
        {
            get => _equipTypeGroupKey;
            set => SetField(ref _equipTypeGroupKey, value);
        }

        public string FileName
        {
            get => _fileName;
            set => SetField(ref _fileName, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => SetField(ref _displayName, value);
        }

        public string FileHash
        {
            get => _fileHash;
            set => SetField(ref _fileHash, value);
        }

        public byte[]? FileData
        {
            get => _fileData;
            set => SetField(ref _fileData, value);
        }

        /// <summary>
        /// Порядок ссылки внутри карточки оборудования.
        /// Для library-коллекций обычно не используется.
        /// </summary>
        public int SortOrder
        {
            get => _sortOrder;
            set => SetField(ref _sortOrder, value);
        }

        public DateTime? UpdatedAt
        {
            get => _updatedAt;
            set => SetField(ref _updatedAt, value);
        }

        /// <summary>
        /// Только для edit-mode списка library.
        /// Показывает, связан ли файл с текущим equipment.
        /// </summary>
        public bool IsLinkedToCurrentEquipment
        {
            get => _isLinkedToCurrentEquipment;
            set => SetField(ref _isLinkedToCurrentEquipment, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
                return;

            field = value;
            OnPropertyChanged(propertyName);
        }
    }
}