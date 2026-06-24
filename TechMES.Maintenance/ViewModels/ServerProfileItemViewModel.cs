namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Одна строка понятного профиля сервера.
/// В отличие от технических appsettings, эта модель показывает оператору готовую выжимку:
/// какой хост используется, какие порты открыты, где лежит publish и какие Windows Services управляются.
/// </summary>
public sealed class ServerProfileItemViewModel : ObservableObject
{
    private string _section = "";
    private string _name = "";
    private string _value = "";
    private string _status = "";
    private string _details = "";

    /// <summary>
    /// Группа параметра: Network, Ports, Certificate, Firewall, Deploy или Services.
    /// </summary>
    public string Section
    {
        get => _section;
        set => SetProperty(ref _section, value);
    }

    /// <summary>
    /// Название параметра в профиле.
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Основное значение, которое оператору обычно нужно увидеть первым.
    /// </summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>
    /// Короткий статус строки профиля: OK, Warning, Error или Info.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Дополнительная подсказка: путь, health URL, firewall rule, publish-папка или пояснение проблемы.
    /// </summary>
    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }
}
