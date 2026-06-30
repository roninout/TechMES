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
    /// Сколько узлов Runtime вернул всего, включая группы, если Runtime их отдаёт.
    /// </summary>
    public int TotalCount { get; init; }
}
