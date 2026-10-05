using System.Globalization;
using System.Net.Http.Json;
using TechMES.Contracts.Alarms;

namespace TechMES.Web.Clients;

/// <summary>
/// WEB получает готовый снимок дня через Runtime; к ODBC он не подключается.
/// </summary>
public sealed class AlarmSummaryApiClient
{
    private readonly IHttpClientFactory _clients;

    public AlarmSummaryApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    public async Task<AlarmSummaryResponse> GetAsync(DateOnly date, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        var client = _clients.CreateClient("RuntimeService");
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"api/alarms/summary?date={day}&version={knownVersion}&refresh={forceRefresh.ToString().ToLowerInvariant()}";

        return await client.GetFromJsonAsync<AlarmSummaryResponse>(url, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty alarm history response.");
    }
}