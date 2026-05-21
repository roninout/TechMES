using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Состояние левой панели:
    /// - список оборудования
    /// - станции
    /// - фильтры
    /// - выбранное оборудование
    /// - прогресс загрузки списка
    /// </summary>
    public sealed class EquipmentListViewModel : ObservableObject
    {
        public ObservableCollection<EquipListBoxItem> Equipments { get; } = new();        
        public ObservableCollection<StationStatusItem> Stations { get; } = new();

        private string _equipName = "";
        public string EquipName
        {
            get => _equipName;
            set => SetProperty(ref _equipName, value);
        }

        private EquipListBoxItem? _selectedListBoxEquipment;
        public EquipListBoxItem? SelectedListBoxEquipment
        {
            get => _selectedListBoxEquipment;
            set => SetProperty(ref _selectedListBoxEquipment, value);
        }

        private EquipTypeGroup _selectedTypeFilter = EquipTypeGroup.All;
        public EquipTypeGroup SelectedTypeFilter
        {
            get => _selectedTypeFilter;
            set => SetProperty(ref _selectedTypeFilter, value);
        }

        private string _selectedStation = "All";
        public string SelectedStation
        {
            get => _selectedStation;
            set => SetProperty(ref _selectedStation, value);
        }

        private int _equipListTotal;
        public int EquipListTotal
        {
            get => _equipListTotal;
            set => SetProperty(ref _equipListTotal, value);
        }

        private int _equipListDone;
        public int EquipListDone
        {
            get => _equipListDone;
            set => SetProperty(ref _equipListDone, value);
        }

        private bool _isEquipListLoading;
        public bool IsEquipListLoading
        {
            get => _isEquipListLoading;
            set => SetProperty(ref _isEquipListLoading, value);
        }

        /// <summary>
        /// Карта "Station+Type -> last selected equipment".
        /// Пока оставляем тут, чтобы позже убрать её из MainWindow.
        /// </summary>
        public Dictionary<string, string> LastEquipByFilterKey { get; } =
            new(System.StringComparer.OrdinalIgnoreCase);
    }
}