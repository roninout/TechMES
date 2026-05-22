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

    private Uri RuntimeBaseUri => new(_configuration["RuntimeService:BaseUrl"] ?? "https://localhost:7101/");

    /// <summary>
    /// Loads the complete Info snapshot for one equipment item. Binary files are
    /// not downloaded here; Runtime.Service returns metadata, and the client adds
    /// stable content URLs for lazy image/PDF loading.
    /// </summary>
    public async Task<EquipmentInfoDto> GetInfoAsync(string equipName, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var result = await client.GetFromJsonAsync<EquipmentInfoDto>(
            $"api/info/{encodedName}",
            ct);

        result ??= new EquipmentInfoDto { EquipName = equipName };
        ApplyContentUrls(result);

        return result;
    }

    /// <summary>
    /// Saves the editable long description and receives a refreshed Info snapshot.
    /// </summary>
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

        if (result is null)
            throw new InvalidOperationException("Runtime Service returned empty info.");

        ApplyContentUrls(result);

        return result;
    }

    /// <summary>
    /// Reads remembered PDF page/zoom for a specific instruction or scheme file.
    /// </summary>
    public async Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(
        string equipName,
        EquipmentInfoFileKind kind,
        long fileId,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);
        var response = await client.GetAsync(
            $"api/info/{encodedName}/document-view/{kind}/{fileId}",
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<EquipmentInfoDocumentViewStateDto>(
            cancellationToken: ct);
    }

    /// <summary>
    /// Persists remembered PDF page/zoom through Runtime.Service.
    /// </summary>
    public async Task<EquipmentInfoDocumentViewStateDto> SaveDocumentViewStateAsync(
        string equipName,
        SaveEquipmentInfoDocumentViewStateRequest request,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipName);

        var response = await client.PostAsJsonAsync(
            $"api/info/{encodedName}/document-view",
            request,
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EquipmentInfoDocumentViewStateDto>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Runtime Service returned empty PDF view state.");
    }

    /// <summary>
    /// Adds one note and passes the current device name for audit fields.
    /// </summary>
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

    /// <summary>
    /// Updates one existing note and returns the normalized server copy.
    /// </summary>
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

    /// <summary>
    /// Deletes one note from the current equipment item.
    /// </summary>
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

    /// <summary>
    /// Converts file metadata into browser-ready URLs. The hash query parameter
    /// lets browser caching stay aggressive while still refreshing changed files.
    /// </summary>
    private void ApplyContentUrls(EquipmentInfoDto info)
    {
        foreach (var file in info.Photos)
        {
            file.Kind = EquipmentInfoFileKind.Photo;
            ApplyContentUrl(file);
        }

        foreach (var file in info.Instructions)
        {
            file.Kind = EquipmentInfoFileKind.Instruction;
            ApplyContentUrl(file);
        }

        foreach (var file in info.Schemes)
        {
            file.Kind = EquipmentInfoFileKind.Scheme;
            ApplyContentUrl(file);
        }
    }

    private void ApplyContentUrl(EquipmentInfoFileDto file)
    {
        var relative = $"api/info/files/{file.Kind}/{file.Id}";

        if (!string.IsNullOrWhiteSpace(file.FileHash))
            relative += $"?v={Uri.EscapeDataString(file.FileHash)}";

        file.ContentUrl = new Uri(RuntimeBaseUri, relative).ToString();
    }
}
