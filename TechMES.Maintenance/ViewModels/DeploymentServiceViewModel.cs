using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка вкладки Deploy для одного публикуемого сервиса.
/// Показывает, из какого проекта публикуем, куда кладем файлы и какой exe будет привязан к Windows Service.
/// </summary>
public sealed class DeploymentServiceViewModel(ServiceDefinition definition) : ObservableObject
{
    private string _publishDirectory = "";
    private string _executablePath = "";
    private string _status = "Ready";
    private bool _isBusy;

    /// <summary>
    /// Исходное описание сервиса из maintenance.settings.json.
    /// </summary>
    public ServiceDefinition Definition { get; } = definition;

    /// <summary>
    /// Название сервиса для интерфейса.
    /// </summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>
    /// Путь к csproj, который будет опубликован.
    /// </summary>
    public string ProjectPath => Definition.ProjectPath ?? "";

    /// <summary>
    /// Имя Windows Service.
    /// </summary>
    public string ServiceName => Definition.ServiceName;

    /// <summary>
    /// Папка публикации конкретного сервиса.
    /// </summary>
    public string PublishDirectory
    {
        get => _publishDirectory;
        set => SetProperty(ref _publishDirectory, value);
    }

    /// <summary>
    /// Полный путь к exe, который будет установлен как Windows Service.
    /// </summary>
    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    /// <summary>
    /// Последний статус операции publish/install/start.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Блокирует повторные действия по строке, пока команда выполняется.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Перечитывает поля, которые берутся напрямую из Definition.
    /// Используется после изменения typed-настроек Maintenance.
    /// </summary>
    public void RefreshDefinitionBindings()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ProjectPath));
        OnPropertyChanged(nameof(ServiceName));
    }
}
