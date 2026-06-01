namespace TechMES.Contracts.Info;

/// <summary>
/// Легкая сводка Info-модуля для одного оборудования.
/// Используется каталогом, чтобы показать иконки наличия фото/PDF/схем/заметок без загрузки полной карточки.
/// </summary>
public sealed class EquipmentInfoSummaryDto
{
    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipName { get; set; } = string.Empty;

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
    /// Количество заметок.
    /// </summary>
    public int NoteCount { get; set; }
}
