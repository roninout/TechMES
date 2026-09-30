using System.Net.Http.Json;
using TechMES.Contracts.Alarms;

namespace TechMES.Web.Clients;

/// <summary>
/// Получает состояние снимка из Runtime.Service; к CtApi WEB не обращается.
/// </summary>
public sealed class ActiveAlarmApiClient
{
    private readonly IHttpClientFactory _clients;

    public ActiveAlarmApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    /// <summary>
    /// Передаёт номер уже показанного снимка. Если он не менялся, Runtime
    /// вернёт NotModified=true без повторной передачи всех аварий.
    /// </summary>
    public async Task<ActiveAlarmsResponse> GetActiveAsync(bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        var client = _clients.CreateClient("RuntimeService");
        var url = $"api/alarms/active?version={knownVersion}&refresh={forceRefresh.ToString().ToLowerInvariant()}";

        return await client.GetFromJsonAsync<ActiveAlarmsResponse>(url, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty alarm response.");
    }
}