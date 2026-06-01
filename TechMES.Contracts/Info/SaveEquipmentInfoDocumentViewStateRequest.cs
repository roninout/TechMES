namespace TechMES.Contracts.Info;

/// <summary>
/// Запрос сохранения положения просмотра PDF/схемы.
/// </summary>
public sealed class SaveEquipmentInfoDocumentViewStateRequest
{
    /// <summary>
    /// Тип файла.
    /// </summary>
    public EquipmentInfoFileKind Kind { get; set; }

    /// <summary>
    /// Идентификатор файла.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Имя файла для диагностики и отображения.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Текущая страница PDF.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// Текущий zoom в процентах.
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
}
