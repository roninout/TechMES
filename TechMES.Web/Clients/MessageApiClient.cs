using System.Net.Http.Json;
using TechMES.Contracts.Messages;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для модуля Messages.
/// Razor-компонент Messages.razor не знает URL endpoint-ов напрямую:
/// вся HTTP-логика собрана здесь.
/// </summary>
public sealed class MessageApiClient
{
    /// <summary>
    /// Фабрика именованного клиента RuntimeService.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Конфигурация WEB-проекта. Нужна для имени текущего клиента/устройства.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Создает клиент Messages API.
    /// </summary>
    public MessageApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Имя WEB-клиента для viewed/author/activity операций.
    /// </summary>
    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    /// <summary>
    /// Возвращает HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    /// <summary>
    /// Загружает сообщения. includeInactive управляет режимом просмотра архива.
    /// deviceName нужен Runtime.Service, чтобы отметить viewed состояние для текущего клиента.
    /// </summary>
    public async Task<MessageListResponse> GetMessagesAsync(bool includeInactive, CancellationToken ct = default)
    {
        var client = CreateClient();

        var url = $"api/messages?includeInactive={includeInactive.ToString().ToLowerInvariant()}&deviceName={Uri.EscapeDataString(DeviceName)}";

        var result = await client.GetFromJsonAsync<MessageListResponse>(url, ct);

        return result ?? new MessageListResponse();
    }

    /// <summary>
    /// Создает или обновляет сообщение.
    /// Runtime.Service сам решает, какие поля Created/Updated заполнить.
    /// </summary>
    public async Task<EquipmentMessageDto> SaveAsync(SaveMessageRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            $"api/messages?deviceName={Uri.EscapeDataString(DeviceName)}",
            request,
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentMessageDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty message.");
    }

    /// <summary>
    /// Отмечает сообщение просмотренным текущим WEB-клиентом.
    /// </summary>
    public async Task MarkViewedAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsync(
            $"api/messages/{messageId}/viewed?deviceName={Uri.EscapeDataString(DeviceName)}",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Переключает active/inactive для сообщения.
    /// Backend проверяет правило авторства и вернет ошибку, если операция запрещена.
    /// </summary>
    public async Task ToggleActiveAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.PostAsync(
            $"api/messages/{messageId}/toggle-active?deviceName={Uri.EscapeDataString(DeviceName)}",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Удаляет сообщение.
    /// </summary>
    public async Task DeleteAsync(long messageId, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.DeleteAsync(
            $"api/messages/{messageId}?deviceName={Uri.EscapeDataString(DeviceName)}",
            ct);

        response.EnsureSuccessStatusCode();
    }
}
