namespace TechMES.Maintenance.Models;

/// <summary>
/// Компактный снимок каталога Runtime для будущих вкладок импорта схем и инструкций.
/// Maintenance не держит здесь CtApi-объекты: только готовые имена станций, типов и оборудования из Runtime HTTP API.
/// </summary>
public sealed class RuntimeCatalogSnapshot
{
    /// <summary>
    /// Станции из Runtime /api/equipment. Нужны для построения связей Station -> document.
    /// </summary>
    public IReadOnlyList<string> Stations { get; init; } = [];

    /// <summary>
    /// Типы оборудования из Runtime /api/equipment. Нужны для связей Type -> document.
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>
    /// Имена оборудования из Runtime /api/equipment. Нужны для связей Equipment -> document.
    /// </summary>
    public IReadOnlyList<string> Equipments { get; init; } = [];

    /// <summary>
    /// Flattened Runtime equipment rows used by Import/Edit tables.
    /// Keeps Station, TypeGroup alias and Equipment name in one row for document links.
    /// </summary>
    public IReadOnlyList<RuntimeCatalogEquipmentItem> EquipmentItems { get; init; } = [];

    /// <summary>
    /// Сколько узлов Runtime вернул всего, включая группы, если Runtime их отдаёт.
    /// </summary>
    public int TotalCount { get; init; }
}

/// <summary>
/// One selectable equipment row from Runtime catalog for Instruction/Scheme links.
/// </summary>
public sealed record RuntimeCatalogEquipmentItem(
    string Station,
    string Type,
    string Equipment);
