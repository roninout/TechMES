namespace TechMES.Contracts.Equipment;

/// <summary>
/// Ответ Runtime.Service со списком оборудования.
/// 
/// Кроме самого списка, сразу отдаём станции и типы,
/// чтобы WEB мог быстро построить фильтры.
/// </summary>
public sealed class EquipmentListResponse
{
    public IReadOnlyList<EquipmentDto> Equipments { get; set; } = [];

    public IReadOnlyList<string> Stations { get; set; } = [];

    public IReadOnlyList<EquipmentTypeGroup> TypeGroups { get; set; } = [];

    public int TotalCount { get; set; }
}