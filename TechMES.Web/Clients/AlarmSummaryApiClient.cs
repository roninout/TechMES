using System.Globalization;
using System.Net.Http.Json;
using TechMES.Contracts.Alarms;

namespace TechMES.Web.Clients;

/// <summary>
/// Получает готовый снимок диапазона через Runtime Service.
/// WEB непосредственно к ODBC не подключается.
/// </summary>
public sealed class AlarmSummaryApiClient
{
    private readonly IHttpClientFactory _clients;

    public AlarmSummaryApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    /// <summary>
    /// Даты включительны. Version позволяет не пересылать неизменённый список.
    /// </summary>
    public async Task<AlarmSummaryResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        var client = _clients.CreateClient("RuntimeService");
        var fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"api/alarms/summary?from={fromText}&to={toText}&version={knownVersion}&refresh={forceRefresh.ToString().ToLowerInvariant()}";

        return await client.GetFromJsonAsync<AlarmSummaryResponse>(url, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty alarm history response.");
    }
}