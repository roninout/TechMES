using TechMES.Contracts.Scada;

namespace TechMES.Application.Scada;

/// <summary>
/// Порт приложения для работы с Plant SCADA.
///
/// Runtime.Service зависит только от этого интерфейса.
/// Реализацией может быть CtApi, Mock или Disabled adapter.
/// </summary>
public interface IPlantScadaGateway
{
    /// <summary>
    /// Инициализирует выбранный Plant SCADA adapter.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Возвращает текущее состояние подключения.
    /// </summary>
    Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Читает один тег. Используется для диагностики.
    /// </summary>
    Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default);

    /// <summary>
    /// Читает набор тегов за один логический вызов gateway.
    ///
    /// CtApi-реализация удерживает общий CtApi gate один раз
    /// на весь batch, а не для каждого отдельного тега.
    /// </summary>
    Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default);

    /// <summary>
    /// Записывает одно значение через выбранный adapter.
    /// </summary>
    Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default);
}