using System.Net.Http.Json;
using TechMES.Contracts.Info;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для Info-модуля.
/// Инкапсулирует работу с карточкой оборудования, файлами, PDF view state и notes.
/// </summary>
public sealed class InfoApiClient
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
    /// Создает клиент Info API.
    /// </summary>
    public InfoApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Имя WEB-клиента для author/audit полей notes.
    /// </summary>
    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    /// <summary>
    /// Загружает полный Info snapshot для одного оборудования.
    /// Бинарные файлы здесь не скачиваются: Runtime.Service возвращает metadata,
    /// а клиент добавляет стабильные ContentUrl для ленивой загрузки image/PDF.
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
    /// Сохраняет редактируемое long description и получает обновленный Info snapshot.
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
    /// Читает сохраненные page/zoom для конкретного PDF instruction или scheme.
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
    /// Сохраняет remembered PDF page/zoom через Runtime.Service.
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
    /// Добавляет одну note и передает имя текущего WEB-клиента для audit/author полей.
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
    /// Обновляет существующую note и возвращает нормализованную server copy.
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
    /// Удаляет одну note у текущего оборудования.
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

    /// <summary>
    /// Возвращает HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    /// <summary>
    /// Преобразует metadata файлов в same-origin WEB URLs.
    /// Это важно для планшетов: браузер должен запрашивать файлы у TechMES.Web,
    /// а не у localhost Runtime.Service на клиентском устройстве.
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

    /// <summary>
    /// Заполняет ContentUrl одного файла и добавляет cache-busting hash, если он есть.
    /// </summary>
    private void ApplyContentUrl(EquipmentInfoFileDto file)
    {
        var relative = $"/api/runtime/info/files/{file.Kind}/{file.Id}";

        if (!string.IsNullOrWhiteSpace(file.FileHash))
            relative += $"?v={Uri.EscapeDataString(file.FileHash)}";

        file.ContentUrl = relative;
    }
}
