using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

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
    /// Clicks inside the popup do not pass through this Window event because Popup is a separate HWND.
    /// </summary>
    private void OnOwnerWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDropDownOpen)
            return;

        if (IsMouseOver || PART_ToggleButton.IsMouseOver || PART_Popup.IsMouseOver)
            return;

        IsDropDownOpen = false;
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
            new PropertyMetadata(false));

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
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

        var filter = PART_FilterTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return option.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        ItemsView.Refresh();
    }

    private void OnItemCheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectedTextFromItems();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
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
        if (PART_FilterTextBox is not null)
            PART_FilterTextBox.Text = "";
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