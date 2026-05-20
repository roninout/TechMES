using System.Net.Http.Json;
using TechMES.Contracts.Messages;

namespace TechMES.Web.Clients;

/// <summary>
/// Клиент для HTTP-запросов к Runtime Service по модулю Messages.
///
/// Важно:
/// - Razor-компонент Messages.razor не должен знать URL endpoint-ов напрямую;
/// - вся работа с HTTP собрана здесь;
/// - если API Runtime Service изменится, править нужно будет этот класс, а не UI.
/// </summary>
public sealed class MessageApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public MessageApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    public async Task<MessageListResponse> GetMessagesAsync(bool includeInactive, CancellationToken ct = default)
    {
        var client = CreateClient();

        var url = $"api/messages?includeInactive={includeInactive.ToString().ToLowerInvariant()}&deviceName={Uri.EscapeDataString(DeviceName)}";

        var result = await client.GetFromJsonAsync<MessageListResponse>(url, ct);

        return result ?? new MessageListResponse();
    }

    public async Task<EquipmentMessageDto> SaveAsync(SaveMessageRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync($"api/messages?deviceName={Uri.EscapeDataString(DeviceName)}", request, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentMessageDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty message.");
    }

    public async Task MarkViewedAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsync($"api/messages/{messageId}/viewed?deviceName={Uri.EscapeDataString(DeviceName)}", content: null, ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task ToggleActiveAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsync($"api/messages/{messageId}/toggle-active?deviceName={Uri.EscapeDataString(DeviceName)}", content: null, ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.DeleteAsync($"api/messages/{messageId}?deviceName={Uri.EscapeDataString(DeviceName)}", ct);

        response.EnsureSuccessStatusCode();
    }
}
