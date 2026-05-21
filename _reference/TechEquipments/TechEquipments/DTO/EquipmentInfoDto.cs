using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    public sealed class EquipmentInfoDto : INotifyPropertyChanged
    {
        private string _equipName = "";
        private string? _productCode;
        private string? _supplier;
        private string? _supplierLogoCachePath;
        private string? _description;
        private DateTime? _updatedAt;

        public string EquipName
        {
            get => _equipName;
            set => SetField(ref _equipName, value);
        }

        public string? ProductCode
        {
            get => _productCode;
            set => SetField(ref _productCode, value);
        }

        public string? Supplier
        {
            get => _supplier;
            set => SetField(ref _supplier, value);
        }

        public string? SupplierLogoCachePath
        {
            get => _supplierLogoCachePath;
            set => SetField(ref _supplierLogoCachePath, value);
        }

        public string? Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        public DateTime? UpdatedAt
        {
            get => _updatedAt;
            set => SetField(ref _updatedAt, value);
        }

        private bool _isFavorite;

        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetField(ref _isFavorite, value);
        }

        public ObservableCollection<EquipmentInfoFileDto> Photos { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> Instructions { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> Schemes { get; } = new();

        /// <summary>
        /// Notes теперь отдельная коллекция, а не одно RTF-поле в equip_info.
        /// </summary>
        public ObservableCollection<EquipmentInfoNoteDto> Notes { get; } = new();

        public static EquipmentInfoDto CreateEmpty(string equipName)
        {
            return new EquipmentInfoDto
            {
                EquipName = (equipName ?? "").Trim()
            };
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