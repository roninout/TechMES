using System;
using System.Collections.ObjectModel;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Состояние вкладки Param.
    /// Пока выносим только bindable-state.
    /// Gate/cancellation/polling-cts пока оставляем в MainWindow/Controller.
    /// </summary>
    public sealed class ParamViewModel : ObservableObject
    {
        public ObservableCollection<ParamItem> ParamItems { get; } = new();
        public ObservableCollection<DiDoRefRow> ParamDiRows { get; } = new();
        public ObservableCollection<DiDoRefRow> ParamDoRows { get; } = new();
        public ObservableCollection<PlcRefRow> ParamPlcRows { get; } = new();

        private object? _currentParamModel;
        public object? CurrentParamModel
        {
            get => _currentParamModel;
            set
            {
                if (!SetProperty(ref _currentParamModel, value))
                    return;

                Raise(nameof(CurrentParamChanel), nameof(IsCurrentParamChanelVisible));
            }
        }

        private int _paramReadCycles;
        public int ParamReadCycles
        {
            get => _paramReadCycles;
            set => SetProperty(ref _paramReadCycles, value);
        }

        private ParamSettingsPage _currentParamSettingsPage = ParamSettingsPage.None;
        public ParamSettingsPage CurrentParamSettingsPage
        {
            get => _currentParamSettingsPage;
            set => SetProperty(ref _currentParamSettingsPage, value);
        }

        private string? _dryRunEquipName;
        public string? DryRunEquipName
        {
            get => _dryRunEquipName;
            set => SetProperty(ref _dryRunEquipName, value);
        }

        private DryRunMotor? _dryRunModel;
        public DryRunMotor? DryRunModel
        {
            get => _dryRunModel;
            set => SetProperty(ref _dryRunModel, value);
        }

        private string? _linkedAtvEquipName;
        public string? LinkedAtvEquipName
        {
            get => _linkedAtvEquipName;
            set => SetProperty(ref _linkedAtvEquipName, value);
        }

        private AtvModel? _linkedAtvModel;
        public AtvModel? LinkedAtvModel
        {
            get => _linkedAtvModel;
            set => SetProperty(ref _linkedAtvModel, value);
        }

        private bool _suppressParamWritesFromPolling;
        public bool SuppressParamWritesFromPolling
        {
            get => _suppressParamWritesFromPolling;
            set => SetProperty(ref _suppressParamWritesFromPolling, value);
        }

        private DateTime _paramReadResumeAtUtc = DateTime.MinValue;
        public DateTime ParamReadResumeAtUtc
        {
            get => _paramReadResumeAtUtc;
            set => SetProperty(ref _paramReadResumeAtUtc, value);
        }

        private bool _suppressParamWritesFromUiRollback;
        public bool SuppressParamWritesFromUiRollback
        {
            get => _suppressParamWritesFromUiRollback;
            set => SetProperty(ref _suppressParamWritesFromUiRollback, value);
        }

        public string CurrentParamChanel
        {
            get
            {
                var ch = (CurrentParamModel as IHasChanel)?.Chanel;
                return FormatChanelForHeader(ch);
            }
        }

        public bool IsCurrentParamChanelVisible
        {
            get
            {
                var ch = (CurrentParamModel as IHasChanel)?.Chanel;
                if (string.IsNullOrWhiteSpace(ch))
                    return false;

                return !ch.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string FormatChanelForHeader(string? raw)
        {
            raw = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            if (raw.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return raw;

            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
                return $"module: {parts[0]}, chanel: {parts[1]}";

            return raw;
        }
    }
}