using System.Net.Http.Json;
using TechMES.Contracts.Info;

namespace TechMES.Web.Clients;

public sealed class InfoApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public InfoApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    public async Task<EquipmentInfoDto> GetInfoAsync(string equipName, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var result = await client.GetFromJsonAsync<EquipmentInfoDto>(
            $"api/info/{encodedName}",
            ct);

        return result ?? new EquipmentInfoDto { EquipName = equipName };
    }

    public async Task<EquipmentInfoDto> SaveDescriptionAsync(
        string equipName,
        string? description,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var response = await client.PutAsJsonAsync(
            $"api/info/{encodedName}/description",
            new SaveEquipmentInfoDescriptionRequest
            {
                Description = description
            },
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentInfoDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty info.");
    }

    public async Task<EquipmentInfoNoteDto> AddNoteAsync(
        string equipName,
        string noteText,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var response = await client.PostAsJsonAsync(
            $"api/info/{encodedName}/notes?deviceName={Uri.EscapeDataString(DeviceName)}",
            new SaveEquipmentInfoNoteRequest
            {
                NoteText = noteText
            },
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentInfoNoteDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty note.");
    }

    public async Task<EquipmentInfoNoteDto> UpdateNoteAsync(
        string equipName,
        long noteId,
        string noteText,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var response = await client.PutAsJsonAsync(
            $"api/info/{encodedName}/notes/{noteId}?deviceName={Uri.EscapeDataString(DeviceName)}",
            new SaveEquipmentInfoNoteRequest
            {
                NoteText = noteText
            },
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentInfoNoteDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty note.");
    }

    public async Task DeleteNoteAsync(string equipName, long noteId, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var response = await client.DeleteAsync(
            $"api/info/{encodedName}/notes/{noteId}",
            ct);

        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }
}
