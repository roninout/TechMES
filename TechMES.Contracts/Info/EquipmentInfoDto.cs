namespace TechMES.Contracts.Info;

/// <summary>
/// Полная Info-карточка оборудования для правой области WEB.
/// </summary>
public sealed class EquipmentInfoDto
{
    /// <summary>
    /// Имя оборудования в SCADA.
    /// </summary>
    public string EquipName { get; set; } = string.Empty;

    /// <summary>
    /// Код изделия/заказной номер.
    /// </summary>
    public string? ProductCode { get; set; }

    /// <summary>
    /// Поставщик оборудования.
    /// </summary>
    public string? Supplier { get; set; }

    /// <summary>
    /// Имя файла логотипа поставщика.
    /// </summary>
    public string? SupplierLogoFileName { get; set; }

    /// <summary>
    /// Data URL логотипа поставщика для отображения без отдельного запроса файла.
    /// </summary>
    public string? SupplierLogoDataUrl { get; set; }

    /// <summary>
    /// Текстовое описание оборудования из Info-модуля.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Дата последнего изменения карточки.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Количество фото.
    /// </summary>
    public int PhotoCount { get; set; }

    /// <summary>
    /// Количество PDF-инструкций.
    /// </summary>
    public int InstructionCount { get; set; }

    /// <summary>
    /// Количество схем.
    /// </summary>
    public int SchemeCount { get; set; }

    /// <summary>
    /// Метаданные фото без бинарных данных.
    /// </summary>
    public List<EquipmentInfoFileDto> Photos { get; set; } = [];

    /// <summary>
    /// Метаданные PDF-инструкций без бинарных данных.
    /// </summary>
    public List<EquipmentInfoFileDto> Instructions { get; set; } = [];

    /// <summary>
    /// Метаданные схем без бинарных данных.
    /// </summary>
    public List<EquipmentInfoFileDto> Schemes { get; set; } = [];

    /// <summary>
    /// Заметки по оборудованию.
    /// </summary>
    public List<EquipmentInfoNoteDto> Notes { get; set; } = [];
}
