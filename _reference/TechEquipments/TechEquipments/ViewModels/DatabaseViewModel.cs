using System;
using System.Collections.ObjectModel;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Состояние DB-вкладок.
    /// </summary>
    public sealed class DatabaseViewModel : ObservableObject
    {
        public ObservableCollection<OperatorActDTO> OperatorActRows { get; } = new();
        public ObservableCollection<AlarmHistoryDTO> AlarmHistoryRows { get; } = new();

        private bool _isDbConnected;
        public bool IsDbConnected
        {
            get => _isDbConnected;
            set => SetProperty(ref _isDbConnected, value);
        }

        private bool _isDbLoading;
        public bool IsDbLoading
        {
            get => _isDbLoading;
            set => SetProperty(ref _isDbLoading, value);
        }

        private DateTime _dbDate = DateTime.Today;
        public DateTime DbDate
        {
            get => _dbDate;
            set => SetProperty(ref _dbDate, value);
        }
    }
}