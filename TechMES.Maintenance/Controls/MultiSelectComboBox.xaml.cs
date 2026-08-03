using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace TechMES.Maintenance.Controls;

/// <summary>
/// Simple WPF multi-select dropdown for editing semicolon-separated text values.
///
/// It is used by Maintenance Import/Edit tables where DB fields store several values
/// in one text column, for example:
///     S01; S02; S03
/// </summary>
public partial class MultiSelectComboBox : UserControl
{
    private readonly ObservableCollection<MultiSelectComboBoxItem> _items = [];
    private bool _isInternalUpdate;
    private Window? _ownerWindow;

    public MultiSelectComboBox()
    {
        ItemsView = CollectionViewSource.GetDefaultView(_items);
        ItemsView.Filter = FilterItem;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Initializes popup data and subscribes to the parent Window mouse event.
    /// Popup has StaysOpen=True, so we close it manually when user clicks outside.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildItems();
        SyncSelectionFromSelectedText();
        UpdateDisplayText();

        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OnOwnerWindowPreviewMouseDown;
            _ownerWindow.PreviewMouseDown += OnOwnerWindowPreviewMouseDown;
        }
    }

    /// <summary>
    /// Detaches parent Window mouse handler to avoid keeping old controls alive.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OnOwnerWindowPreviewMouseDown;
            _ownerWindow = null;
        }
    }

    /// <summary>
    /// Closes dropdown when user clicks outside the editor.
    /// Popup is rendered in a separate WPF window, therefore PART_Popup.IsMouseOver
    /// is not reliable enough. We check Popup.Child.IsMouseOver instead.
    /// </summary>
    private void OnOwnerWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDropDownOpen)
            return;

        if (IsMouseInsideEditorOrPopup())
            return;

        IsDropDownOpen = false;
    }

    /// <summary>
    /// Returns true when mouse is over the cell editor itself or over the popup content.
    /// This prevents closing the popup when operator clicks Search, CheckBox or Clear.
    /// </summary>
    private bool IsMouseInsideEditorOrPopup()
    {
        if (IsMouseOver || PART_ToggleButton.IsMouseOver)
            return true;

        if (PART_Popup.Child is FrameworkElement popupChild
            && popupChild.IsMouseOver)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Items visible in the popup list.
    /// </summary>
    public ICollectionView ItemsView { get; }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(MultiSelectComboBox),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedTextProperty =
        DependencyProperty.Register(
            nameof(SelectedText),
            typeof(string),
            typeof(MultiSelectComboBox),
            new FrameworkPropertyMetadata(
                "",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedTextChanged));

    /// <summary>
    /// Semicolon-separated selected values.
    /// </summary>
    public string SelectedText
    {
        get => (string?)GetValue(SelectedTextProperty) ?? "";
        set => SetValue(SelectedTextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(MultiSelectComboBox),
            new PropertyMetadata("Select...", OnPlaceholderChanged));

    public string Placeholder
    {
        get => (string?)GetValue(PlaceholderProperty) ?? "Select...";
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(
            nameof(DisplayText),
            typeof(string),
            typeof(MultiSelectComboBox),
            new PropertyMetadata("Select..."));

    public string DisplayText
    {
        get => (string?)GetValue(DisplayTextProperty) ?? "";
        private set => SetValue(DisplayTextProperty, value);
    }

    public static readonly DependencyProperty IsDropDownOpenProperty =
        DependencyProperty.Register(
            nameof(IsDropDownOpen),
            typeof(bool),
            typeof(MultiSelectComboBox),
            new PropertyMetadata(false, OnIsDropDownOpenChanged));

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    public static readonly DependencyProperty FilterTextProperty =
    DependencyProperty.Register(
        nameof(FilterText),
        typeof(string),
        typeof(MultiSelectComboBox),
        new PropertyMetadata("", OnFilterTextChanged));

    /// <summary>
    /// Keeps keyboard focus inside the DataGrid cell editor when dropdown opens.
    /// Filter typing is handled by PreviewTextInput, not by a real TextBox.
    /// </summary>
    private static void OnIsDropDownOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not MultiSelectComboBox control)
            return;

        if ((bool)e.NewValue)
        {
            control.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => control.PART_ToggleButton.Focus()));
        }
        else
        {
            control.SetCurrentValue(FilterTextProperty, "");
        }
    }

    /// <summary>
    /// Text entered by operator to filter visible dropdown items.
    /// This is not a real TextBox focus value, because DataGrid closes cell editing
    /// when keyboard focus moves into Popup.
    /// </summary>
    public string FilterText
    {
        get => (string?)GetValue(FilterTextProperty) ?? "";
        set => SetValue(FilterTextProperty, value);
    }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MultiSelectComboBox control)
        {
            control.RebuildItems();
            control.SyncSelectionFromSelectedText();
            control.UpdateDisplayText();
        }
    }

    private static void OnSelectedTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MultiSelectComboBox control)
        {
            if (control._isInternalUpdate)
                return;

            control.SyncSelectionFromSelectedText();
            control.UpdateDisplayText();
        }
    }

    private static void OnPlaceholderChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MultiSelectComboBox control)
            control.UpdateDisplayText();
    }

    private void RebuildItems()
    {
        if (_isInternalUpdate)
            return;

        var selectedValues = SplitValues(SelectedText).ToList();

        var sourceValues = new List<string>();
        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                var value = item?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    sourceValues.Add(value);
            }
        }

        /*
         * Важный момент:
         * если в БД уже есть значение, которого сейчас нет в Runtime,
         * мы всё равно показываем его в списке, чтобы оператор видел текущие данные.
         */
        var allValues = sourceValues
            .Concat(selectedValues)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _isInternalUpdate = true;
        try
        {
            _items.Clear();

            foreach (var value in allValues)
            {
                _items.Add(new MultiSelectComboBoxItem
                {
                    Text = value,
                    IsSelected = selectedValues.Contains(value, StringComparer.OrdinalIgnoreCase)
                });
            }
        }
        finally
        {
            _isInternalUpdate = false;
        }

        ItemsView.Refresh();
    }

    private void SyncSelectionFromSelectedText()
    {
        var selectedValues = SplitValues(SelectedText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _isInternalUpdate = true;
        try
        {
            foreach (var item in _items)
                item.IsSelected = selectedValues.Contains(item.Text);
        }
        finally
        {
            _isInternalUpdate = false;
        }

        ItemsView.Refresh();
    }

    private void UpdateSelectedTextFromItems()
    {
        if (_isInternalUpdate)
            return;

        var selectedText = string.Join(
            "; ",
            _items
                .Where(x => x.IsSelected)
                .Select(x => x.Text)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        _isInternalUpdate = true;
        try
        {
            SetCurrentValue(SelectedTextProperty, selectedText);
        }
        finally
        {
            _isInternalUpdate = false;
        }

        UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        var normalized = string.Join(
            "; ",
            SplitValues(SelectedText)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        DisplayText = string.IsNullOrWhiteSpace(normalized)
            ? Placeholder
            : normalized;
    }

    private bool FilterItem(object item)
    {
        if (item is not MultiSelectComboBoxItem option)
            return false;

        var filter = FilterText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return option.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refreshes visible items when pseudo-filter text changes.
    /// </summary>
    private static void OnFilterTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MultiSelectComboBox control)
            control.ItemsView.Refresh();
    }

    private void OnItemCheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectedTextFromItems();
    }

    /// <summary>
    /// Clears selected values before focus can leave the DataGrid editing cell.
    /// We use PreviewMouseDown instead of Click because Click may not fire when DataGrid
    /// commits/cancels editing after focus moves to the Popup button.
    /// </summary>
    private void OnClearPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ClearSelection();

        // Оставляем dropdown открытым, чтобы оператор сразу видел очищенный список.
        IsDropDownOpen = true;

        // Не даём клику увести фокус из DataGrid editor до выполнения очистки.
        e.Handled = true;
    }

    /// <summary>
    /// Keeps focus on the dropdown toggle when operator clicks the pseudo search field.
    /// A real TextBox focus would close DataGrid cell editing.
    /// </summary>
    private void OnFilterBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDropDownOpen)
            IsDropDownOpen = true;

        PART_ToggleButton.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Adds typed characters to the pseudo filter while dropdown is open.
    /// </summary>
    private void OnFilterPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsDropDownOpen)
            return;

        if (string.IsNullOrEmpty(e.Text))
            return;

        SetCurrentValue(FilterTextProperty, FilterText + e.Text);
        e.Handled = true;
    }

    /// <summary>
    /// Handles filter editing keys without moving focus into Popup.
    /// </summary>
    private void OnFilterPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsDropDownOpen)
            return;

        switch (e.Key)
        {
            case Key.Back:
                if (FilterText.Length > 0)
                    SetCurrentValue(FilterTextProperty, FilterText[..^1]);

                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Escape:
                SetCurrentValue(FilterTextProperty, "");
                e.Handled = true;
                break;

            case Key.Space:
                SetCurrentValue(FilterTextProperty, FilterText + " ");
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Clears all selected items and writes an empty string back to SelectedText binding.
    /// </summary>
    private void ClearSelection()
    {
        _isInternalUpdate = true;
        try
        {
            foreach (var item in _items)
                item.IsSelected = false;

            SetCurrentValue(SelectedTextProperty, "");
        }
        finally
        {
            _isInternalUpdate = false;
        }

        UpdateDisplayText();
        ItemsView.Refresh();
    }

    private void OnPopupClosed(object sender, EventArgs e)
    {
        SetCurrentValue(FilterTextProperty, "");
    }

    private static IEnumerable<string> SplitValues(string? value)
    {
        return (value ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private sealed class MultiSelectComboBoxItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}