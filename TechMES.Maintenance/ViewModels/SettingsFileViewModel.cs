using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Файл настроек, отображаемый на вкладке Settings.
/// Содержимое редактируется как JSON-текст, а сохранение выполняется через SettingsFileService.
/// </summary>
public sealed class SettingsFileViewModel(SettingsFileDefinition definition, string fullPath) : ObservableObject
{
    private string _content = "";
    private string _status = "Not loaded";
    private bool _isDirty;

    /// <summary>
    /// Описание файла из maintenance.settings.json.
    /// </summary>
    public SettingsFileDefinition Definition { get; } = definition;

    /// <summary>
    /// Название файла в списке.
    /// </summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>
    /// Полный путь к файлу на диске.
    /// </summary>
    public string FullPath { get; } = fullPath;

    /// <summary>
    /// JSON-текст файла.
    /// </summary>
    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
                IsDirty = true;
        }
    }

    /// <summary>
    /// Статус загрузки или сохранения файла.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// true, если пользователь изменил текст после последней загрузки/сохранения.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    /// <summary>
    /// Устанавливает содержимое без пометки файла как измененного.
    /// Используется после чтения с диска и после успешного сохранения.
    /// </summary>
    public void SetCleanContent(string content)
    {
        _content = content;
        OnPropertyChanged(nameof(Content));
        IsDirty = false;
    }
}
