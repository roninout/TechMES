namespace TechMES.Contracts.Info;

/// <summary>
/// Метаданные файла Info-модуля без бинарного содержимого.
/// </summary>
public sealed class EquipmentInfoFileDto
{
    /// <summary>
    /// Идентификатор файла в библиотечной таблице.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Имя оборудования, к которому привязан файл.
    /// </summary>
    public string EquipName { get; set; } = string.Empty;

    /// <summary>
    /// Ограничение по типу оборудования, если файл общий для группы типов.
    /// </summary>
    public string EquipTypeGroupKey { get; set; } = string.Empty;

    /// <summary>
    /// Физическое/исходное имя файла.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Имя для отображения в WEB.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Хэш файла для cache/version logic.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// Порядок отображения внутри вкладки.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Дата последнего изменения файла.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Тип файла: фото, PDF-инструкция или схема.
    /// </summary>
    public EquipmentInfoFileKind Kind { get; set; }

    /// <summary>
    /// HTTP content type.
    /// </summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// WEB URL для ленивой загрузки бинарного содержимого.
    /// </summary>
    public string ContentUrl { get; set; } = string.Empty;
}
