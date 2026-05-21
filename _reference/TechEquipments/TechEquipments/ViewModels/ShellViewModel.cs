namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Состояние "оболочки" приложения:
    /// - нижний статус/прогресс
    /// - overlay
    /// - состояние связи CtApi
    /// - текущий пользователь CtApi
    /// </summary>
    public sealed class ShellViewModel : ObservableObject
    {
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (!SetProperty(ref _isLoading, value))
                    return;

                Raise(nameof(LoadingText));
            }
        }

        private int _loadedCount;
        public int LoadedCount
        {
            get => _loadedCount;
            set => SetProperty(ref _loadedCount, value);
        }

        private int _currentCount;
        public int CurrentCount
        {
            get => _currentCount;
            set => SetProperty(ref _currentCount, value);
        }

        private int _totalTrends;
        public int TotalTrends
        {
            get => _totalTrends;
            set
            {
                if (!SetProperty(ref _totalTrends, value))
                    return;

                Raise(nameof(LoadingText));
            }
        }

        private int _currentTrendIndex;
        public int CurrentTrendIndex
        {
            get => _currentTrendIndex;
            set
            {
                if (!SetProperty(ref _currentTrendIndex, value))
                    return;

                Raise(nameof(LoadingText));
            }
        }

        private string _currentTrendName = "";
        public string CurrentTrendName
        {
            get => _currentTrendName;
            set
            {
                if (!SetProperty(ref _currentTrendName, value))
                    return;

                Raise(nameof(LoadingText));
            }
        }

        private string _bottomText = "";
        public string BottomText
        {
            get => _bottomText;
            set => SetProperty(ref _bottomText, value);
        }

        private bool _isGlobalProgressActive;
        public bool IsGlobalProgressActive
        {
            get => _isGlobalProgressActive;
            set => SetProperty(ref _isGlobalProgressActive, value);
        }

        private string _globalProgressText = "";
        public string GlobalProgressText
        {
            get => _globalProgressText;
            set => SetProperty(ref _globalProgressText, value);
        }

        private int _globalProgressDone;
        public int GlobalProgressDone
        {
            get => _globalProgressDone;
            set => SetProperty(ref _globalProgressDone, value);
        }

        private int _globalProgressTotal;
        public int GlobalProgressTotal
        {
            get => _globalProgressTotal;
            set => SetProperty(ref _globalProgressTotal, value);
        }

        private bool _isCtApiConnected = true;
        public bool IsCtApiConnected
        {
            get => _isCtApiConnected;
            set => SetProperty(ref _isCtApiConnected, value);
        }

        private string _ctApiStatusText = "";
        public string CtApiStatusText
        {
            get => _ctApiStatusText;
            set => SetProperty(ref _ctApiStatusText, value);
        }

        private string _currentCtUserName = "";
        public string CurrentCtUserName
        {
            get => _currentCtUserName;
            set
            {
                if (!SetProperty(ref _currentCtUserName, value))
                    return;

                Raise(nameof(WindowTitle));
            }
        }

        public string WindowTitle =>
            string.IsNullOrWhiteSpace(CurrentCtUserName)
                ? "TechEquipments"
                : $"TechEquipments ({CurrentCtUserName})";

        private string _paramStatusText = "";
        public string ParamStatusText
        {
            get => _paramStatusText;
            set => SetProperty(ref _paramStatusText, value);
        }

        private bool _isParamCenterLoading;
        public bool IsParamCenterLoading
        {
            get => _isParamCenterLoading;
            //set => SetProperty(ref _isParamCenterLoading, value);
            set
            {
                if (!SetProperty(ref _isParamCenterLoading, value))
                    return;

                // ShouldShowParamOverlay зависит от IsParamCenterLoading, поэтому при изменении loading-состояния нужно обновить binding overlay.
                Raise(nameof(ShouldShowParamOverlay));
            }
        }

        private bool _useParamAreaOverlay = true;
        public bool UseParamAreaOverlay
        {
            get => _useParamAreaOverlay;
            set
            {
                if (!SetProperty(ref _useParamAreaOverlay, value))
                    return;

                // ShouldShowParamOverlay также зависит от настройки Global:Overlay.
                Raise(nameof(ShouldShowParamOverlay));
            }
        }

        public bool ShouldShowParamOverlay => UseParamAreaOverlay && IsParamCenterLoading;

        public int PerTrendMax => 1000;

        public int TotalMax => 100;

        public string LoadingText =>
            IsLoading ? $"{CurrentTrendIndex}/{TotalTrends}: {CurrentTrendName}" : "";
    }
}