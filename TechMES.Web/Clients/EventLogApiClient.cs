using System.Net.Http.Json;
using TechMES.Contracts.EventLog;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для Operation actions и Alarm history.
/// Данные читает Runtime.Service из EventPicker/PostgreSQL.
/// </summary>
public sealed class EventLogApiClient
{
    /// <summary>
    /// Фабрика именованного клиента RuntimeService.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Создает клиент журналов событий.
    /// </summary>
    public EventLogApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Загружает действия операторов за выбранную дату.
    /// Фильтр оборудования передается как query string только если он заполнен.
    /// </summary>
    public async Task<OperatorActionsResponse> GetOperatorActionsAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url = $"api/event-log/operator-actions?date={Uri.EscapeDataString(date.Date.ToString("yyyy-MM-dd"))}";

        if (!string.IsNullOrWhiteSpace(equipmentFilter))
            url += $"&equipmentFilter={Uri.EscapeDataString(equipmentFilter.Trim())}";

        return await client.GetFromJsonAsync<OperatorActionsResponse>(url, ct)
               ?? new OperatorActionsResponse { Date = date.Date, EquipmentFilter = equipmentFilter };
    }

    /// <summary>
    /// Загружает историю тревог за выбранную дату.
    /// </summary>
    public async Task<AlarmHistoryResponse> GetAlarmHistoryAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url = $"api/event-log/alarm-history?date={Uri.EscapeDataString(date.Date.ToString("yyyy-MM-dd"))}";

        if (!string.IsNullOrWhiteSpace(equipmentFilter))
            url += $"&equipmentFilter={Uri.EscapeDataString(equipmentFilter.Trim())}";

        return await client.GetFromJsonAsync<AlarmHistoryResponse>(url, ct)
               ?? new AlarmHistoryResponse { Date = date.Date, EquipmentFilter = equipmentFilter };
    }

    /// <summary>
    /// Возвращает HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }
}
