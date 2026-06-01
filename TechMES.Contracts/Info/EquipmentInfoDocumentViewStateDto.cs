namespace TechMES.Contracts.Info;

/// <summary>
/// Запомненное положение просмотра PDF/схемы для оборудования.
/// </summary>
public sealed class EquipmentInfoDocumentViewStateDto
{
    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipName { get; set; } = string.Empty;

    /// <summary>
    /// Тип файла, для которого сохранено состояние.
    /// </summary>
    public EquipmentInfoFileKind Kind { get; set; }

    /// <summary>
    /// Идентификатор файла.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Имя файла на момент сохранения.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Номер страницы PDF.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// Масштаб просмотра в процентах.
    /// </summary>
    public double ZoomFactor { get; set; } = 100;

    /// <summary>
    /// Горизонтальный якорь/смещение просмотра.
    /// </summary>
    public double AnchorX { get; set; }

    /// <summary>
    /// Вертикальный якорь/смещение просмотра.
    /// </summary>
    public double AnchorY { get; set; }

    /// <summary>
    /// Дата последнего сохранения состояния.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
