namespace TechMES.Contracts.Equipment;

/// <summary>
/// Ответ Runtime.Service со списком оборудования.
/// 
/// Кроме самого списка, сразу отдаём станции и типы,
/// чтобы WEB мог быстро построить фильтры.
/// </summary>
public sealed class EquipmentListResponse
{
    /// <summary>
    /// Полный список узлов оборудования для дерева/списка.
    /// </summary>
    public IReadOnlyList<EquipmentDto> Equipments { get; set; } = [];

    /// <summary>
    /// Список станций для фильтра.
    /// </summary>
    public IReadOnlyList<string> Stations { get; set; } = [];

    /// <summary>
    /// Список групп типов оборудования для фильтра.
    /// </summary>
    public IReadOnlyList<EquipmentTypeGroup> TypeGroups { get; set; } = [];

    /// <summary>
    /// Общее количество узлов.
    /// </summary>
    public int TotalCount { get; set; }
}
