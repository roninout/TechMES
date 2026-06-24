namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Одна строка диагностической проверки окружения.
/// Maintenance использует такие строки во вкладке Checks, чтобы перед запуском или после deploy быстро увидеть,
/// какие внешние зависимости доступны, а где нужна ручная настройка сервера.
/// </summary>
public sealed class DependencyCheckViewModel : ObservableObject
{
    private string _category = "";
    private string _name = "";
    private string _status = "";
    private string _details = "";
    private DateTime _checkedAt;

    /// <summary>
    /// Логическая группа проверки: HTTP, PostgreSQL, CtApi, Files.
    /// </summary>
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>
    /// Человеческое имя проверяемого объекта.
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Короткий результат проверки: OK, Warning, Error или Info.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Подробность результата: путь, URL, порт, причина ошибки или подсказка оператору.
    /// </summary>
    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    /// <summary>
    /// Локальное время, когда проверка была выполнена.
    /// </summary>
    public DateTime CheckedAt
    {
        get => _checkedAt;
        set => SetProperty(ref _checkedAt, value);
    }
}
