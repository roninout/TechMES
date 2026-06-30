using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка вкладки SUPPLIER.
/// Повторяет лист SUPPLIER из Excel-импорта: имя поставщика, имя файла логотипа и сами данные логотипа.
/// </summary>
public sealed class ImportSupplierRowViewModel : ObservableObject
{
    private string _supplier = "";
    private string _logoFileName = "";
    private string _logoStatus = "";
    private ImageSource? _logoPreview;
    private byte[]? _pendingLogoData;
    private bool _logoChanged;
    private bool _isPendingDelete;

    /// <summary>
    /// Имя поставщика. В БД это поле хранится как public.equip_supplier.name и является уникальным ключом.
    /// </summary>
    public string Supplier
    {
        get => _supplier;
        set => SetProperty(ref _supplier, value);
    }

    /// <summary>
    /// Имя файла логотипа. Хранится отдельно от bytea-данных, чтобы оператор видел исходное имя файла.
    /// </summary>
    public string LogoFileName
    {
        get => _logoFileName;
        set => SetProperty(ref _logoFileName, value);
    }

    /// <summary>
    /// Человеческий статус логотипа: уже есть в БД, выбран новый файл или логотип отсутствует.
    /// </summary>
    public string LogoStatus
    {
        get => _logoStatus;
        set => SetProperty(ref _logoStatus, value);
    }

    /// <summary>
    /// Превью логотипа для таблицы SUPPLIER. Строится из logo_data БД или из выбранного оператором файла.
    /// </summary>
    public ImageSource? LogoPreview
    {
        get => _logoPreview;
        set => SetProperty(ref _logoPreview, value);
    }

    /// <summary>
    /// Новые бинарные данные логотипа, выбранные оператором через файл.
    /// До нажатия Save они находятся только в памяти Maintenance.
    /// </summary>
    public byte[]? PendingLogoData
    {
        get => _pendingLogoData;
        set => SetProperty(ref _pendingLogoData, value);
    }

    /// <summary>
    /// Флаг, что логотип в этой строке был выбран заново и при сохранении должен перезаписать logo_data.
    /// </summary>
    public bool LogoChanged
    {
        get => _logoChanged;
        set => SetProperty(ref _logoChanged, value);
    }

    /// <summary>
    /// Флаг отложенного удаления. Строка только перечеркивается в UI, а физическое удаление из БД происходит после Save.
    /// </summary>
    public bool IsPendingDelete
    {
        get => _isPendingDelete;
        set => SetProperty(ref _isPendingDelete, value);
    }

    /// <summary>
    /// Создает замороженный WPF BitmapImage из bytea-данных логотипа.
    /// Freeze нужен, чтобы изображение безопасно жило в UI после закрытия потока.
    /// </summary>
    public void SetLogoPreview(byte[]? logoData)
    {
        if (logoData is null || logoData.Length == 0)
        {
            LogoPreview = null;
            return;
        }

        using var stream = new MemoryStream(logoData);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        LogoPreview = bitmap;
    }
}
