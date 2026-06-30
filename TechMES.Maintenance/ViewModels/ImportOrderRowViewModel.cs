namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка вкладки ORDERS.
/// Повторяет лист ORDERS из Excel-импорта: тип, product code, поставщик, PDF-источник, описание и картинка.
/// </summary>
public sealed class ImportOrderRowViewModel : ObservableObject
{
    private string _type = "";
    private string _productCode = "";
    private string _supplier = "";
    private string _source = "";
    private string _description = "";
    private string _image = "";

    /// <summary>
    /// Тип оборудования из Excel/БД. Позже будет связан с каталогом Runtime.
    /// </summary>
    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    /// <summary>
    /// Код заказа/изделия. В БД public.equip_order.product_code является уникальным ключом.
    /// </summary>
    public string ProductCode
    {
        get => _productCode;
        set => SetProperty(ref _productCode, value);
    }

    /// <summary>
    /// Имя поставщика. При сохранении Maintenance найдет или создаст строку public.equip_supplier и запишет supplier_id.
    /// </summary>
    public string Supplier
    {
        get => _supplier;
        set => SetProperty(ref _supplier, value);
    }

    /// <summary>
    /// PDF-файл или список PDF-файлов, как в колонке Source Excel-листа ORDERS.
    /// </summary>
    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    /// <summary>
    /// Описание заказа/документа, которое затем используется Info-модулем.
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// Изображение или список изображений из Excel-колонки Image.
    /// </summary>
    public string Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }
}
