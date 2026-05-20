using TechMES.Contracts.Scada;

namespace TechMES.Application.Scada;

/// <summary>
/// Порт приложения для работы с Plant SCADA.
/// 
/// Runtime.Service будет использовать только этот интерфейс.
/// Конкретная реализация может быть:
/// - MockPlantScadaGateway для разработки;
/// - CtApiPlantScadaGateway для реального Plant SCADA;
/// - другой adapter в будущем.
/// </summary>
public interface IPlantScadaGateway
{
    /// <summary>
    /// Инициализация adapter-а.
    /// 
    /// Для CtApi здесь будет:
    /// - поиск CtApi.dll;
    /// - настройка DLL directory;
    /// - открытие соединения;
    /// - запуск health monitor.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить состояние подключения к Plant SCADA.
    /// </summary>
    Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Прочитать tag по имени.
    /// 
    /// Пример tagName:
    /// S01_H01_P01_RUN
    /// или реальное имя tag-а после TagInfo.
    /// </summary>
    Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default);

    /// <summary>
    /// Записать значение в tag.
    /// 
    /// Все записи должны идти через Runtime.Service,
    /// чтобы потом можно было централизованно проверять права и логировать действия.
    /// </summary>
    Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default);
}