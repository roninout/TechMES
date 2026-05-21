using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Только состояние для биндинга XAML (оси, режимы, точки, статус).
    /// ЛОГИКИ тут нет.
    /// </summary>
    public sealed class ParamTrendVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ===== UI visibility =====

        private bool _isChartVisible = true;
        public bool IsChartVisible
        {
            get => _isChartVisible;
            set
            {
                if (_isChartVisible == value) return;
                _isChartVisible = value;
                Raise();
                Raise(nameof(IsSettingsVisible));
            }
        }

        public bool IsSettingsVisible => !IsChartVisible;

        // ===== Live/History mode =====

        private bool _isLiveMode = true;

        /// <summary>
        /// Live: ось X pinned к now-window..now.
        /// History: пользователь двигает/зумит ось (DevExpress Scroll/Zoom).
        /// </summary>
        public bool IsLiveMode
        {
            get => _isLiveMode;
            set
            {
                if (_isLiveMode == value) return;
                _isLiveMode = value;
                Raise();
                Raise(nameof(ModeText));
            }
        }

        public string ModeText => IsLiveMode ? "Live" : "History";

        private bool _autoLive = true; // default = включено, если нет в appsettings
        /// <summary>
        /// AutoLive:
        /// true  - когда дотянулись до правого края (последние данные) -> авто-переход в Live
        /// false - авто-переход запрещён, только ручная кнопка Live
        /// </summary>
        public bool AutoLive
        {
            get => _autoLive;
            set
            {
                if (_autoLive == value) return;
                _autoLive = value;
                Raise();
            }
        }

        // ===== Trend time window (Live window size) =====

        private int _windowMinutes = 30;

        /// <summary>
        /// Размер окна Live-режима (минут).
        /// В History этот параметр НЕ задаёт VisualRange напрямую — там управляет пользователь.
        /// </summary>
        public int WindowMinutes
        {
            get => _windowMinutes;
            set { if (_windowMinutes == value) return; _windowMinutes = value; Raise(); }
        }

        // ===== Axis X (VisualRange) =====

        private DateTime _axisXMin;
        public DateTime AxisXMin { get => _axisXMin; set { _axisXMin = value; Raise(); } }

        private DateTime _axisXMax;
        public DateTime AxisXMax { get => _axisXMax; set { _axisXMax = value; Raise(); } }

        // ===== Axis X (WholeRange) =====

        private DateTime _axisXWholeMin;
        public DateTime AxisXWholeMin { get => _axisXWholeMin; set { _axisXWholeMin = value; Raise(); } }

        private DateTime _axisXWholeMax;
        public DateTime AxisXWholeMax { get => _axisXWholeMax; set { _axisXWholeMax = value; Raise(); } }

        // ===== Axis Y =====

        private double _axisYMin;
        public double AxisYMin { get => _axisYMin; set { if (_axisYMin == value) return; _axisYMin = value; Raise(); } }

        private double _axisYMax;
        public double AxisYMax { get => _axisYMax; set { if (_axisYMax == value) return; _axisYMax = value; Raise(); } }

        // ===== Data =====

        public ObservableCollection<TrendPoint> Points { get; } = new();

        // ===== Status =====

        private string _statusText = "";
        public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }
    }
}
