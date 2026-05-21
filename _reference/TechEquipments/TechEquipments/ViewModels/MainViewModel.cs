using System;
using System.Windows.Media;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Корневой ViewModel MainWindow.
    /// Содержит дочерние секции состояния и вычисляемые UI-свойства,
    /// чтобы MainWindow оставался только bridge/view-слоем.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        public ShellViewModel Shell { get; } = new();
        public EquipmentListViewModel EquipmentList { get; } = new();
        public ParamViewModel Param { get; } = new();
        public InfoViewModel Info { get; } = new();
        public MessageViewModel Message { get; } = new();
        public DatabaseViewModel Database { get; } = new();

        public MainViewModel()
        {
            Shell.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ShellViewModel.IsLoading):
                        Raise(nameof(CanMainAction));
                        break;

                    case nameof(ShellViewModel.UseParamAreaOverlay):
                    case nameof(ShellViewModel.IsParamCenterLoading):
                    case nameof(ShellViewModel.ParamStatusText):
                    case nameof(ShellViewModel.BottomText):
                    case nameof(ShellViewModel.IsCtApiConnected):
                    case nameof(ShellViewModel.CtApiStatusText):
                    case nameof(ShellViewModel.IsGlobalProgressActive):
                    case nameof(ShellViewModel.GlobalProgressText):
                    case nameof(ShellViewModel.GlobalProgressDone):
                    case nameof(ShellViewModel.GlobalProgressTotal):
                        RaiseBottomBarComputed();
                        break;
                }
            };

            EquipmentList.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(EquipmentListViewModel.EquipListDone):
                    case nameof(EquipmentListViewModel.EquipListTotal):
                    case nameof(EquipmentListViewModel.IsEquipListLoading):
                        RaiseBottomBarComputed();
                        break;

                    case nameof(EquipmentListViewModel.SelectedListBoxEquipment):
                        Raise(nameof(IsGroupNodeSelected), nameof(IsSoeTabVisible));
                        break;
                }
            };

            EquipmentList.Equipments.CollectionChanged += (_, __) =>
            {
                Raise(nameof(EquipListText), nameof(BottomText), nameof(BottomStatusText));
            };

            Database.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(DatabaseViewModel.IsDbLoading):
                        Raise(nameof(CanMainAction));
                        RaiseBottomBarComputed();
                        break;

                    case nameof(DatabaseViewModel.IsDbConnected):
                        Raise(nameof(CanMainAction));
                        break;
                }
            };

            Message.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(MessageViewModel.ActiveMessageCount):
                        Raise(nameof(MessageViewModel.TabHeader));
                        break;
                }
            };
        }

        private MainTabKind _selectedMainTab = MainTabKind.Param;
        public MainTabKind SelectedMainTab
        {
            get => _selectedMainTab;
            set
            {
                if (!SetProperty(ref _selectedMainTab, value))
                    return;

                Raise(nameof(SelectedMainTabIndex));
                RaiseSelectedTabComputed();
                RaiseBottomBarComputed();
            }
        }

        public int SelectedMainTabIndex
        {
            get => (int)SelectedMainTab;
            set
            {
                var tab = (MainTabKind)value;
                if (SelectedMainTab == tab)
                    return;

                SelectedMainTab = tab;
            }
        }

        // ===== toolbar / tabs =====

        public bool IsDbTabSelected => SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

        public string MainActionButtonText => SelectedMainTab switch
        {
            MainTabKind.SOE => "Load",
            MainTabKind.OperationActions => "Search",
            MainTabKind.AlarmHistory => "Search",
            MainTabKind.Info => "",
            MainTabKind.Message => "",
            _ => "Run",
        };

        public bool CanMainAction => SelectedMainTab switch
        {
            MainTabKind.SOE => !Shell.IsLoading,
            MainTabKind.Info => false,
            MainTabKind.Message => false,
            MainTabKind.Param => false,
            _ => Database.IsDbConnected && !Database.IsDbLoading,
        };

        public bool ShowToolbarScanQrButton => SelectedMainTab == MainTabKind.Param;

        public bool ShowMainActionButton =>
            SelectedMainTab != MainTabKind.Param &&
            SelectedMainTab != MainTabKind.Info &&
            SelectedMainTab != MainTabKind.Message;

        // ===== bottom bar =====

        public bool IsGroupNodeSelected => EquipmentList.SelectedListBoxEquipment?.IsGroup == true;

        public bool IsSoeTabVisible => !IsGroupNodeSelected;

        public int EquipListMax => Math.Max(1, EquipmentList.EquipListTotal);

        public string EquipListText =>
            EquipmentList.IsEquipListLoading
                ? $"Loading equipments: {EquipmentList.EquipListDone}/{EquipmentList.EquipListTotal}"
                : $"Equipments: {EquipmentList.Equipments.Count}";

        public string ParamBottomLoadingText =>
            string.IsNullOrWhiteSpace(Shell.ParamStatusText)
                ? "Updating data..."
                : Shell.ParamStatusText;

        public bool IsBottomLoading =>
            EquipmentList.IsEquipListLoading ||
            Database.IsDbLoading ||
            Shell.IsGlobalProgressActive ||
            (!Shell.UseParamAreaOverlay &&
             SelectedMainTab == MainTabKind.Param &&
             Shell.IsParamCenterLoading);

        public string BottomText
        {
            get
            {
                if (EquipmentList.IsEquipListLoading)
                    return EquipListText;

                if (Shell.IsGlobalProgressActive)
                    return string.IsNullOrWhiteSpace(Shell.GlobalProgressText)
                        ? "Working..."
                        : Shell.GlobalProgressText;

                if (!Shell.UseParamAreaOverlay &&
                    SelectedMainTab == MainTabKind.Param &&
                    Shell.IsParamCenterLoading)
                {
                    return ParamBottomLoadingText;
                }

                return Shell.BottomText;
            }
        }

        public bool IsBottomStatusVisible => IsBottomLoading || !Shell.IsCtApiConnected;

        public bool IsBottomProgressVisible =>
                    EquipmentList.IsEquipListLoading ||
                    Database.IsDbLoading ||
                    Shell.IsGlobalProgressActive ||
                    (!Shell.UseParamAreaOverlay &&
                     SelectedMainTab == MainTabKind.Param &&
                     Shell.IsParamCenterLoading);

        public bool BottomProgressIsIndeterminate =>
                    (Database.IsDbLoading && !EquipmentList.IsEquipListLoading && !Shell.IsGlobalProgressActive)
                    || ((!Shell.UseParamAreaOverlay &&
                         SelectedMainTab == MainTabKind.Param &&
                         Shell.IsParamCenterLoading &&
                         !EquipmentList.IsEquipListLoading &&
                         !Shell.IsGlobalProgressActive));

        public string BottomStatusText =>
            !Shell.IsCtApiConnected && !string.IsNullOrWhiteSpace(Shell.CtApiStatusText)
                ? Shell.CtApiStatusText
                : BottomText;

        public Brush BottomStatusBrush =>
            !Shell.IsCtApiConnected ? Brushes.Red : Brushes.Black;

        public int BottomProgressMaximum
        {
            get
            {
                if (EquipmentList.IsEquipListLoading)
                    return EquipListMax;

                if (Shell.IsGlobalProgressActive)
                    return Math.Max(1, Shell.GlobalProgressTotal);

                return 100;
            }
        }

        public int BottomProgressValue
        {
            get
            {
                if (EquipmentList.IsEquipListLoading)
                    return EquipmentList.EquipListDone;

                if (Shell.IsGlobalProgressActive)
                    return Shell.GlobalProgressDone;

                return 0;
            }
        }

        private void RaiseSelectedTabComputed()
        {
            Raise(
                nameof(IsDbTabSelected),
                nameof(MainActionButtonText),
                nameof(CanMainAction),
                nameof(ShowToolbarScanQrButton),
                nameof(ShowMainActionButton));
        }

        private void RaiseBottomBarComputed()
        {
            Raise(
                nameof(EquipListMax),
                nameof(EquipListText),
                nameof(ParamBottomLoadingText),
                nameof(IsBottomLoading),
                nameof(BottomText),
                nameof(IsBottomStatusVisible),
                nameof(BottomStatusText),
                nameof(BottomStatusBrush),
                nameof(IsBottomProgressVisible),
                nameof(BottomProgressIsIndeterminate),
                nameof(BottomProgressMaximum),
                nameof(BottomProgressValue));
        }
    }
}