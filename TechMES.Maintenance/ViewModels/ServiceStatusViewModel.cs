using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка таблицы статусов сервисов.
/// Хранит и статическое описание сервиса, и живые данные последней проверки.
/// </summary>
public sealed class ServiceStatusViewModel(ServiceDefinition definition) : ObservableObject
{
    private string _status = "Unknown";
    private string _health = "Not checked";
    private string _details = "";
    private DateTime? _lastChecked;
    private bool _isBusy;

    /// <summary>
    /// Конфигурация сервиса из maintenance.settings.json.
    /// </summary>
    public ServiceDefinition Definition { get; } = definition;

    /// <summary>
    /// Имя сервиса для пользователя.
    /// </summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>
    /// Имя Windows Service для sc.exe.
    /// </summary>
    public string ServiceName => Definition.ServiceName;

    /// <summary>
    /// Health URL, который можно проверить HTTP-запросом.
    /// </summary>
    public string HealthUrl => Definition.HealthUrl ?? "";

    /// <summary>
    /// Последний известный статус Windows Service.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Последний известный результат HTTP health-check.
    /// </summary>
    public string Health
    {
        get => _health;
        set => SetProperty(ref _health, value);
    }

    /// <summary>
    /// Диагностические подробности последней операции.
    /// </summary>
    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    /// <summary>
    /// Время последней проверки.
    /// </summary>
    public DateTime? LastChecked
    {
        get => _lastChecked;
        set => SetProperty(ref _lastChecked, value);
    }

    /// <summary>
    /// Флаг, блокирующий повторное нажатие кнопок во время операции.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Уведомляет UI, что статические поля Definition были изменены через typed Settings.
    /// Сам объект Definition остается тем же, поэтому без явного события WPF не перечитает HealthUrl/DisplayName.
    /// </summary>
    public void RefreshDefinitionBindings()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ServiceName));
        OnPropertyChanged(nameof(HealthUrl));
    }
}
