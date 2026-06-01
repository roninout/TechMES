namespace TechMES.Contracts.Info;

/// <summary>
/// Бинарное содержимое файла Info-модуля.
/// Используется отдельным endpoint-ом, чтобы не таскать blob-данные вместе с карточкой оборудования.
/// </summary>
public sealed class EquipmentInfoFileContentDto
{
    /// <summary>
    /// Идентификатор файла в библиотечной таблице.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Тип файла.
    /// </summary>
    public EquipmentInfoFileKind Kind { get; set; }

    /// <summary>
    /// Исходное имя файла.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Имя файла для отображения/скачивания.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Хэш файла для cache/version logic.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// HTTP content type.
    /// </summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Бинарные данные файла.
    /// </summary>
    public byte[] FileData { get; set; } = [];

    /// <summary>
    /// Дата последнего изменения файла.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
