namespace TechMES.Contracts.Info;

/// <summary>
/// Тип файла Info-модуля.
/// </summary>
public enum EquipmentInfoFileKind
{
    /// <summary>
    /// Фото оборудования.
    /// </summary>
    Photo = 0,

    /// <summary>
    /// PDF-инструкция.
    /// </summary>
    Instruction = 1,

    /// <summary>
    /// PDF/файл схемы.
    /// </summary>
    Scheme = 2
}
