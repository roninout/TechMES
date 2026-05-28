using System.Net.Http.Json;
using TechMES.Contracts.EventLog;

namespace TechMES.Web.Clients;

public sealed class EventLogApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public EventLogApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

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

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }
}
