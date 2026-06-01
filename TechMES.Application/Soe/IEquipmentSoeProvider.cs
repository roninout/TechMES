using TechMES.Contracts.Equipment;
using TechMES.Contracts.Soe;

namespace TechMES.Application.Soe;

/// <summary>
/// Application-интерфейс чтения SOE-событий выбранного оборудования.
/// Runtime.Service зависит от этого интерфейса, а конкретная реализация находится в CtApi infrastructure.
/// </summary>
public interface IEquipmentSoeProvider
{
    /// <summary>
    /// Загружает SOE-строки для оборудования и, если нужно, связанных reference-элементов из каталога.
    /// </summary>
    Task<SoeResponse> GetSoeAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        int perTrendMax = 1000,
        int totalMax = 100,
        CancellationToken ct = default);
}
