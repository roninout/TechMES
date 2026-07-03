namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка вкладки INSTRUCTION.
///
/// Station, Type и Equipment приходят из Runtime Catalog.
/// Пользователь выбирает только Product code, после чего Supplier,
/// Source, Description и Image автоматически подставляются из ORDERS.
/// </summary>
public sealed class ImportInstructionRowViewModel : ObservableObject
{
    private string _station = "";
    private string _type = "";
    private string _equipment = "";
    private string _productCode = "";
    private string _supplier = "";
    private string _source = "";
    private string _description = "";
    private string _image = "";
    private ImportOrderRowViewModel? _selectedOrder;

    public string Station
    {
        get => _station;
        set => SetProperty(ref _station, value);
    }

    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public string Equipment
    {
        get => _equipment;
        set => SetProperty(ref _equipment, value);
    }

    /// <summary>
    /// Product code, связанный с Equipment.
    /// Это единственное редактируемое поле строки INSTRUCTION.
    /// </summary>
    public string ProductCode
    {
        get => _productCode;
        set
        {
            if (!SetProperty(ref _productCode, value))
                return;

            /*
             * Если ProductCode изменили не через SelectedOrder
             * (например, вставкой из Excel), прежний объект ORDERS
             * больше не соответствует строке.
             */
            if (_selectedOrder is not null
                && !string.Equals(
                    _selectedOrder.ProductCode,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                _selectedOrder = null;
                OnPropertyChanged(nameof(SelectedOrder));
            }
        }
    }

    /// <summary>
    /// Выбранная строка ORDERS.
    /// ComboBox отображает ProductCode, но передаёт в модель всю строку,
    /// чтобы связанные read-only поля обновлялись одновременно.
    /// </summary>
    public ImportOrderRowViewModel? SelectedOrder
    {
        get => _selectedOrder;
        set => ApplyOrder(value, value?.ProductCode);
    }

    public string Supplier
    {
        get => _supplier;
        set => SetProperty(ref _supplier, value);
    }

    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    /// <summary>
    /// Применяет данные выбранной строки ORDERS.
    ///
    /// fallbackProductCode нужен для уже сохранённого equip_info.product_code,
    /// если соответствующая строка ORDERS была удалена или ещё не загружена.
    /// </summary>
    public void ApplyOrder(
        ImportOrderRowViewModel? order,
        string? fallbackProductCode = null)
    {
        if (!ReferenceEquals(_selectedOrder, order))
        {
            _selectedOrder = order;
            OnPropertyChanged(nameof(SelectedOrder));
        }

        ProductCode =
            order?.ProductCode
            ?? fallbackProductCode?.Trim()
            ?? "";

        Supplier = order?.Supplier ?? "";
        Source = order?.Source ?? "";
        Description = order?.Description ?? "";
        Image = order?.Image ?? "";
    }
}
