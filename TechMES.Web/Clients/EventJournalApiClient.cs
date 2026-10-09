using System.Globalization;
using System.Net.Http.Json;
using TechMES.Contracts.Events;

namespace TechMES.Web.Clients;

/// <summary>
/// Получает готовый снимок событий через Runtime Service; WEB не открывает ODBC.
/// </summary>
public sealed class EventJournalApiClient
{
    private readonly IHttpClientFactory _clients;

    public EventJournalApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    /// <summary>
    /// Передаёт включительные даты и известную версию снимка.
    /// </summary>
    public async Task<EventJournalResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        var client = _clients.CreateClient("RuntimeService");
        var fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"api/events/journal?from={fromText}&to={toText}&version={knownVersion}&refresh={forceRefresh.ToString().ToLowerInvariant()}";

        return await client.GetFromJsonAsync<EventJournalResponse>(url, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty event journal response.");
    }
}