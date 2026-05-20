using System.Net.Http.Json;
using TechMES.Contracts.Runtime;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент для получения состояния Runtime.Service.
///
/// WEB не проверяет PostgreSQL напрямую.
/// Он спрашивает Runtime.Service через /api/health.
/// </summary>
public sealed class RuntimeStatusApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public RuntimeStatusApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    public async Task<RuntimeHealthResponse> GetHealthAsync(
        CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.GetAsync("api/health", ct);

        var health = await response.Content.ReadFromJsonAsync<RuntimeHealthResponse>(
            cancellationToken: ct);

        if (health is null)
        {
            return new RuntimeHealthResponse
            {
                Status = "ERROR",
                Database = "Unknown",
                Error = "Runtime Service returned empty health response.",
                Time = DateTime.Now
            };
        }

        return health;
    }
}