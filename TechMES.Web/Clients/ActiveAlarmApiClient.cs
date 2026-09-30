using System.Net.Http.Json;
using TechMES.Contracts.Alarms;

namespace TechMES.Web.Clients;

/// <summary>
/// WEB обращается к Runtime.Service; напрямую CtApi.dll в WEB не загружается.
/// </summary>
public sealed class ActiveAlarmApiClient
{
    private readonly IHttpClientFactory _clients;

    public ActiveAlarmApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    /// <summary>
    /// Загружает один снимок текущей сводки.
    /// </summary>
    public async Task<ActiveAlarmsResponse> GetActiveAsync(CancellationToken ct = default)
    {
        var client = _clients.CreateClient("RuntimeService");

        return await client.GetFromJsonAsync<ActiveAlarmsResponse>("api/alarms/active", ct) ?? throw new InvalidOperationException("Runtime returned an empty alarm response.");
    }
}