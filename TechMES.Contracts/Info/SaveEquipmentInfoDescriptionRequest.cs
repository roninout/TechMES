namespace TechMES.Contracts.Info;

/// <summary>
/// Запрос сохранения описания оборудования.
/// </summary>
public sealed class SaveEquipmentInfoDescriptionRequest
{
    /// <summary>
    /// Новый текст описания. Пустое значение очищает описание.
    /// </summary>
    public string? Description { get; set; }
}
