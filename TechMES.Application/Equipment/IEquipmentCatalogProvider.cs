using TechMES.Contracts.Equipment;

namespace TechMES.Application.Equipment;

/// <summary>
/// Порт приложения для получения каталога оборудования.
/// 
/// Runtime.Service будет работать только с этим интерфейсом.
/// Сейчас реализация будет InMemory.
/// Позже появится CtApiEquipmentCatalogProvider.
/// </summary>
public interface IEquipmentCatalogProvider
{
    /// <summary>
    /// Инициализация provider-а.
    /// 
    /// Для InMemory — заполнение тестовых данных.
    /// Для CtApi — подключение/первичная загрузка оборудования.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить весь список оборудования.
    /// 
    /// На первом этапе фильтрацию делаем в WEB,
    /// позже можно перенести фильтрацию на Runtime.Service.
    /// </summary>
    Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить одно оборудование по имени.
    /// </summary>
    Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default);
}