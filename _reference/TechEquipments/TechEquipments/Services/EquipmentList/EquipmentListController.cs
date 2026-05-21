using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using TechEquipments.ViewModels;
using DevExpress.Xpf.Grid;

namespace TechEquipments
{
    /// <summary>
    /// Логика левой панели:
    /// - ICollectionView + фильтр/сортировка
    /// - debounce search
    /// - память последнего выбранного equipment для пары Station+Type
    /// - выбор/восстановление после смены фильтра
    ///
    /// ВАЖНО:
    /// orchestration-побочки (Info reload / Param polling / overlay)
    /// сюда не переносим — это остаётся у MainWindow.
    /// </summary>
    public sealed class EquipmentListController
    {
        private readonly MainViewModel _vm;
        private readonly Dispatcher _dispatcher;
        private readonly Func<TreeListControl?> _getTreeList;

        public ICollectionView EquipmentsView { get; private set; } = null!;

        private readonly DispatcherTimer _searchTimer;
        private string _pendingSearch = "";

        private bool _suppressEquipNameFromSelection;
        public bool SuppressEquipNameFromSelection => _suppressEquipNameFromSelection;

        private EquipmentListViewModel EquipVm => _vm.EquipmentList;
        public int EquipmentsCount => EquipVm.Equipments.Count(x => x.IsPlainEquipmentNode);

        public EquipmentListController(MainViewModel vm, Dispatcher dispatcher, Func<TreeListControl?> getTreeList)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _getTreeList = getTreeList ?? throw new ArgumentNullException(nameof(getTreeList));

            _searchTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(150),
                DispatcherPriority.Normal,
                OnSearchTimerTick,
                _dispatcher);

            _searchTimer.Stop();
        }

        /// <summary>
        /// Полностью заменяет список оборудования и список станций.
        /// UI/orchestration-побочки не вызывает.
        /// </summary>
        public void ReplaceLoadedEquipments(IReadOnlyCollection<EquipListBoxItem> items)
        {
            EquipVm.Equipments.Clear();
            foreach (var it in items)
                EquipVm.Equipments.Add(it);

            EquipVm.Stations.Clear();
            EquipVm.Stations.Add(new StationStatusItem
            {
                Name = "All",
                IsOffline = false
            });

            foreach (var group in items
                .Where(x => x.IsPlainEquipmentNode && !string.IsNullOrWhiteSpace(x.Station))
                .GroupBy(x => x.Station.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                // Берём первое подходящее equipment этой станции.
                // Предпочитаем то, у которого есть Tag.
                var probe = group.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Tag)) ?? group.First();

                EquipVm.Stations.Add(new StationStatusItem
                {
                    Name = group.Key,
                    ProbeEquipmentName = (probe.Equipment ?? "").Trim(),
                    ProbeTagName = (probe.Tag ?? "").Trim(),
                    IsOffline = false
                });
            }
            NormalizeSelectedStation();
        }

        /// <summary>
        /// Если выбранная станция исчезла после перезагрузки списка,
        /// откатываемся на All.
        /// </summary>
        public void NormalizeSelectedStation()
        {
            if (!EquipVm.Stations.Any(s =>
                    string.Equals(s.Name, EquipVm.SelectedStation, StringComparison.OrdinalIgnoreCase)))
            {
                EquipVm.SelectedStation = "All";
            }
        }

        public List<StationStatusItem> GetStationProbeItems()
        {
            return EquipVm.Stations
                .Where(s => !string.Equals(s.Name, "All", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Комбинированный core:
        /// - обновить фильтр
        /// - восстановить/выбрать equipment
        /// Side effects по-прежнему вызываются снаружи.
        /// </summary>
        public FilterSelectionResult ApplyFiltersAndRestoreSelectionCore()
        {
            // ВАЖНО:
            // TreeListControl при Refresh фильтра может сам выбрать первую видимую строку.
            // Если в этот момент не подавить SelectedItemChanged, первый элемент
            // будет записан в EquipName/LastEquipByFilterKey раньше нашего restore.
            _suppressEquipNameFromSelection = true;

            try
            {
                ApplyFilters();
                return RestoreOrSelectEquipmentAfterFilterChangedCore();
            }
            finally
            {
                _suppressEquipNameFromSelection = false;
            }
        }

        public void InitEquipmentsView()
        {
            EquipmentsView = CollectionViewSource.GetDefaultView(EquipVm.Equipments);
            EquipmentsView.Filter = FilterEquipment;

            EquipmentsView.SortDescriptions.Clear();
            EquipmentsView.SortDescriptions.Add(
                new SortDescription(nameof(EquipListBoxItem.DisplayText), ListSortDirection.Ascending));
        }

        public void InitSearchTimer()
        {
            // Таймер уже создан в ctor.
            // Метод оставляем, чтобы шаг миграции был очевидным и совместимым с текущим MainWindow.
        }

        private void OnSearchTimerTick(object? sender, EventArgs e)
        {
            _searchTimer.Stop();
            DoIncrementalSearch(_pendingSearch);
        }

        public void ScheduleSearch(string? text)
        {
            _pendingSearch = text ?? "";
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        public void DoIncrementalSearch(string? text)
        {
            if (EquipmentsView == null)
                return;

            text = (text ?? "").Trim();
            if (text.Length == 0)
                return;

            var match =
                EquipmentsView.Cast<object>()
                    .OfType<EquipListBoxItem>()
                    .FirstOrDefault(x => (x.Equipment ?? "")
                        .StartsWith(text, StringComparison.OrdinalIgnoreCase))
                ?? EquipmentsView.Cast<object>()
                    .OfType<EquipListBoxItem>()
                    .FirstOrDefault(x => (x.Equipment ?? "")
                        .Contains(text, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return;

            _suppressEquipNameFromSelection = true;
            try
            {
                EquipVm.SelectedListBoxEquipment = match;
                EquipmentsView.MoveCurrentTo(match);

                _dispatcher.BeginInvoke(new Action(() =>
                {
                    ScrollEquipmentIntoView(match);
                }), DispatcherPriority.Background);
            }
            finally
            {
                _suppressEquipNameFromSelection = false;
            }
        }

        public bool FilterEquipment(object obj)
        {
            if (obj is not EquipListBoxItem it)
                return false;

            var st = (EquipVm.SelectedStation ?? "").Trim();
            if (string.IsNullOrWhiteSpace(st))
                st = "All";

            // Режим Equipment:
            // показываем только group nodes и child nodes.
            if (EquipVm.SelectedTypeFilter == EquipTypeGroup.Equipment)
            {
                if (it.IsGroup)
                    return GroupHasVisibleChildren(it, st);

                if (it.IsEquipmentChildNode)
                    return StationMatches(it, st);

                return false;
            }

            // Режим Favorites:
            // показываем только обычное оборудование, отмеченное как favorite.
            if (EquipVm.SelectedTypeFilter == EquipTypeGroup.Favorites)
            {
                if (!it.IsPlainEquipmentNode)
                    return false;

                if (!StationMatches(it, st))
                    return false;

                return it.IsFavorite;
            }

            // Для всех остальных режимов работаем как раньше,
            // но только по обычным equipment nodes.
            if (!it.IsPlainEquipmentNode)
                return false;

            if (!StationMatches(it, st))
                return false;

            if (EquipVm.SelectedTypeFilter == EquipTypeGroup.All)
                return true;

            return it.TypeGroup == EquipVm.SelectedTypeFilter;
        }

        public void ApplyFilters()
        {
            EquipmentsView?.Refresh();
        }

        public string BuildFilterSelectionKey(string? station, EquipTypeGroup typeGroup)
        {
            var st = string.IsNullOrWhiteSpace(station) ? "All" : station.Trim();
            return $"{st}|{typeGroup}";
        }

        public void RememberEquipmentForCurrentFilters(string? equipName)
        {
            var eq = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(eq))
                return;

            var key = BuildFilterSelectionKey(EquipVm.SelectedStation, EquipVm.SelectedTypeFilter);
            EquipVm.LastEquipByFilterKey[key] = eq;
        }

        public void ImportRememberedEquipmentsByFilter(Dictionary<string, string>? state)
        {
            EquipVm.LastEquipByFilterKey.Clear();

            if (state == null || state.Count == 0)
                return;

            foreach (var pair in state)
            {
                var key = (pair.Key ?? "").Trim();
                var equip = (pair.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(equip))
                    continue;

                EquipVm.LastEquipByFilterKey[key] = equip;
            }
        }

        public Dictionary<string, string> ExportRememberedEquipmentsByFilter()
        {
            var existingEquipments = EquipVm.Equipments
                .Select(x => (x.Equipment ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in EquipVm.LastEquipByFilterKey)
            {
                var key = (pair.Key ?? "").Trim();
                var equip = (pair.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(equip))
                    continue;

                if (!existingEquipments.Contains(equip))
                    continue;

                result[key] = equip;
            }

            return result;
        }

        /// <summary>
        /// Возвращает результат применения фильтра/выбора.
        /// Побочные orchestration-эффекты вызываются уже снаружи, в MainWindow.
        /// </summary>
        public FilterSelectionResult RestoreOrSelectEquipmentAfterFilterChangedCore()
        {
            if (EquipmentsView == null)
                return FilterSelectionResult.NoVisible();

            var visibleItems = EquipmentsView
                .Cast<object>()
                .OfType<EquipListBoxItem>()
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .ToList();

            if (visibleItems.Count == 0)
            {
                EquipVm.SelectedListBoxEquipment = null;

                var hadEquipName = !string.IsNullOrWhiteSpace(EquipVm.EquipName);
                if (hadEquipName)
                    EquipVm.EquipName = "";

                return FilterSelectionResult.NoVisible(hadEquipName);
            }

            var key = BuildFilterSelectionKey(EquipVm.SelectedStation, EquipVm.SelectedTypeFilter);

            EquipVm.LastEquipByFilterKey.TryGetValue(key, out var rememberedEquip);
            rememberedEquip = (rememberedEquip ?? "").Trim();

            var currentEquip = (EquipVm.EquipName ?? "").Trim();

            EquipListBoxItem? rememberedMatch = null;
            if (!string.IsNullOrWhiteSpace(rememberedEquip))
            {
                rememberedMatch = visibleItems.FirstOrDefault(x =>
                    string.Equals((x.Equipment ?? "").Trim(), rememberedEquip, StringComparison.OrdinalIgnoreCase));

                if (rememberedMatch == null)
                    EquipVm.LastEquipByFilterKey.Remove(key);
            }

            EquipListBoxItem? currentMatch = null;
            if (!string.IsNullOrWhiteSpace(currentEquip))
            {
                currentMatch = visibleItems.FirstOrDefault(x =>
                    string.Equals((x.Equipment ?? "").Trim(), currentEquip, StringComparison.OrdinalIgnoreCase));
            }

            var match = rememberedMatch ?? currentMatch ?? visibleItems[0];
            var selectedEquip = (match.Equipment ?? "").Trim();

            EquipVm.SelectedListBoxEquipment = match;
            EquipmentsView.MoveCurrentTo(match);

            var equipNameChanged =
                !string.Equals((EquipVm.EquipName ?? "").Trim(), selectedEquip, StringComparison.OrdinalIgnoreCase);

            if (equipNameChanged)
                EquipVm.EquipName = selectedEquip;

            RememberEquipmentForCurrentFilters(selectedEquip);

            _dispatcher.BeginInvoke(new Action(() =>
            {
                ScrollEquipmentIntoView(match);
            }), DispatcherPriority.Background);

            return FilterSelectionResult.Selected(selectedEquip, equipNameChanged);
        }

        public sealed class FilterSelectionResult
        {
            public bool HasVisibleItems { get; }
            public bool EquipNameChanged { get; }
            public string? SelectedEquipName { get; }

            private FilterSelectionResult(bool hasVisibleItems, bool equipNameChanged, string? selectedEquipName)
            {
                HasVisibleItems = hasVisibleItems;
                EquipNameChanged = equipNameChanged;
                SelectedEquipName = selectedEquipName;
            }

            public static FilterSelectionResult NoVisible(bool equipNameChanged = false)
                => new(false, equipNameChanged, null);

            public static FilterSelectionResult Selected(string selectedEquipName, bool equipNameChanged)
                => new(true, equipNameChanged, selectedEquipName);
        }

        /// <summary>
        /// Прокрутка выбранного equipment в TreeListControl.
        /// Для мягкой миграции используем поиск row по data object.
        /// </summary>
        private void ScrollEquipmentIntoView(EquipListBoxItem? item)
        {
            if (item == null)
                return;

            var tree = _getTreeList();
            if (tree == null)
                return;

            var rowHandle = tree.FindRow(item);
            if (rowHandle < 0)
                return;

            tree.View.FocusedRowHandle = rowHandle;
            tree.View.ScrollIntoView(rowHandle);
        }

        private static bool StationMatches(EquipListBoxItem it, string selectedStation)
        {
            if (string.Equals(selectedStation, "All", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(
                (it.Station ?? "").Trim(),
                selectedStation,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool GroupHasVisibleChildren(EquipListBoxItem groupNode, string selectedStation)
        {
            return EquipVm.Equipments.Any(x =>
                x.IsEquipmentChildNode &&
                string.Equals(x.ParentNodeId, groupNode.NodeId, StringComparison.OrdinalIgnoreCase) &&
                StationMatches(x, selectedStation));
        }
    }
}