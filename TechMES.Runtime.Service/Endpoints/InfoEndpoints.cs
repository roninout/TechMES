using TechMES.Application.Info;
using TechMES.Contracts.Info;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API Info-модуля.
/// Здесь отдаются карточка оборудования, файлы, PDF view state, описание и notes.
/// Бинарные данные грузятся отдельным endpoint-ом, чтобы не раздувать основной DTO.
/// </summary>
public static class InfoEndpoints
{
    /// <summary>
    /// Подключает endpoints Info-модуля.
    /// </summary>
    public static IEndpointRouteBuilder MapInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info/{equipName}", GetInfoAsync);
        app.MapGet("/api/info/files/{kind}/{id:long}", GetInfoFileAsync);
        app.MapGet("/api/info/{equipName}/document-view/{kind}/{fileId:long}", GetDocumentViewStateAsync);
        app.MapPost("/api/info/{equipName}/document-view", SaveDocumentViewStateAsync);
        app.MapPut("/api/info/{equipName}/description", SaveDescriptionAsync);
        app.MapPost("/api/info/{equipName}/notes", AddNoteAsync);
        app.MapPut("/api/info/{equipName}/notes/{noteId:long}", UpdateNoteAsync);
        app.MapDelete("/api/info/{equipName}/notes/{noteId:long}", DeleteNoteAsync);

        return app;
    }

    /// <summary>
    /// Возвращает один нормализованный snapshot Info-модуля:
    /// поля карточки, metadata файлов, счетчики, notes и логотип supplier.
    /// </summary>
    private static async Task<IResult> GetInfoAsync(
        string equipName,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        var info = await infoStore.GetAsync(equipName, ct);
        return Results.Ok(info);
    }

    /// <summary>
    /// Отдает image/PDF bytes по id файла.
    /// ETag/Last-Modified включают browser cache, а range processing нужен PDF viewer-у.
    /// </summary>
    private static async Task<IResult> GetInfoFileAsync(
        string kind,
        long id,
        IEquipmentInfoStore infoStore,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryParseFileKind(kind, out var fileKind))
            return Results.BadRequest("Unknown file kind.");

        var file = await infoStore.GetFileAsync(fileKind, id, ct);
        if (file is null || file.FileData.Length == 0)
            return Results.NotFound();

        var etag = BuildEntityTag(file);
        var lastModified = file.UpdatedAt?.ToUniversalTime();

        SetCacheHeaders(httpContext, file, etag, lastModified);

        if (ClientCacheIsFresh(httpContext, etag, lastModified))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.File(
            file.FileData,
            file.ContentType,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// Читает сохраненную позицию PDF/документа, аналог WPF remembered document view.
    /// </summary>
    private static async Task<IResult> GetDocumentViewStateAsync(
        string equipName,
        string kind,
        long fileId,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        if (!TryParseFileKind(kind, out var fileKind))
            return Results.BadRequest("Unknown file kind.");

        var state = await infoStore.GetDocumentViewStateAsync(
            equipName,
            fileKind,
            fileId,
            ct);

        return state is null
            ? Results.NotFound()
            : Results.Ok(state);
    }

    /// <summary>
    /// Сохраняет page/zoom для PDF-подобных файлов.
    /// Photo отклоняется, потому что у изображения нет page position.
    /// </summary>
    private static async Task<IResult> SaveDocumentViewStateAsync(
        string equipName,
        SaveEquipmentInfoDocumentViewStateRequest request,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipName))
            return Results.BadRequest("Equipment name is required.");

        if (request.Kind == EquipmentInfoFileKind.Photo)
            return Results.BadRequest("Photo does not support PDF view state.");

        if (request.FileId <= 0)
            return Results.BadRequest("File id is required.");

        var state = await infoStore.SaveDocumentViewStateAsync(equipName, request, ct);
        return Results.Ok(state);
    }

    /// <summary>
    /// Сохраняет редактируемое long description и возвращает обновленный Info DTO.
    /// </summary>
    private static async Task<IResult> SaveDescriptionAsync(
        string equipName,
        SaveEquipmentInfoDescriptionRequest request,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipName))
            return Results.BadRequest("Equipment name is required.");

        var info = await infoStore.SaveDescriptionAsync(
            equipName,
            request.Description,
            ct);

        return Results.Ok(info);
    }

    /// <summary>
    /// Добавляет note. Автор определяется по deviceName WEB-клиента или Runtime.DeviceName.
    /// </summary>
    private static async Task<IResult> AddNoteAsync(
        string equipName,
        SaveEquipmentInfoNoteRequest request,
        string? deviceName,
        IEquipmentInfoStore infoStore,
        IAppRuntimeContext runtime,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipName))
            return Results.BadRequest("Equipment name is required.");

        if (string.IsNullOrWhiteSpace(request.NoteText))
            return Results.BadRequest("Note text is required.");

        var note = await infoStore.AddNoteAsync(
            equipName,
            request.NoteText,
            ResolveActorName(deviceName, runtime),
            ct);

        return Results.Ok(note);
    }

    /// <summary>
    /// Обновляет одну note и оставляет служебные поля author/audit на backend стороне.
    /// </summary>
    private static async Task<IResult> UpdateNoteAsync(
        string equipName,
        long noteId,
        SaveEquipmentInfoNoteRequest request,
        string? deviceName,
        IEquipmentInfoStore infoStore,
        IAppRuntimeContext runtime,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipName))
            return Results.BadRequest("Equipment name is required.");

        if (string.IsNullOrWhiteSpace(request.NoteText))
            return Results.BadRequest("Note text is required.");

        var note = await infoStore.UpdateNoteAsync(
            equipName,
            noteId,
            request.NoteText,
            ResolveActorName(deviceName, runtime),
            ct);

        return note is null
            ? Results.NotFound()
            : Results.Ok(note);
    }

    /// <summary>
    /// Удаляет одну note по id. Выбор следующей видимой note остается задачей UI.
    /// </summary>
    private static async Task<IResult> DeleteNoteAsync(
        string equipName,
        long noteId,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        await infoStore.DeleteNoteAsync(equipName, noteId, ct);
        return Results.Ok();
    }

    /// <summary>
    /// Определяет имя автора для операций notes.
    /// </summary>
    private static string ResolveActorName(string? deviceName, IAppRuntimeContext runtime)
    {
        return string.IsNullOrWhiteSpace(deviceName)
            ? runtime.DeviceName
            : deviceName.Trim();
    }

    /// <summary>
    /// Безопасно разбирает строковый kind из URL в enum EquipmentInfoFileKind.
    /// </summary>
    private static bool TryParseFileKind(string? value, out EquipmentInfoFileKind kind)
    {
        return Enum.TryParse(value, ignoreCase: true, out kind)
               && Enum.IsDefined(kind);
    }

    /// <summary>
    /// Строит ETag для file endpoint. Если БД не дала hash, используем kind/id/updatedAt.
    /// </summary>
    private static string BuildEntityTag(EquipmentInfoFileContentDto file)
    {
        var value = string.IsNullOrWhiteSpace(file.FileHash)
            ? $"{file.Kind}-{file.Id}-{file.UpdatedAt:O}"
            : file.FileHash.Trim();

        return $"\"{value.Replace("\"", string.Empty)}\"";
    }

    /// <summary>
    /// Выставляет cache и content-disposition headers для браузера/PDF viewer.
    /// </summary>
    private static void SetCacheHeaders(
        HttpContext httpContext,
        EquipmentInfoFileContentDto file,
        string etag,
        DateTime? lastModified)
    {
        var headers = httpContext.Response.Headers;

        headers.CacheControl = "public, max-age=86400";
        headers.ETag = etag;
        headers.ContentDisposition =
            $"inline; filename=\"{NormalizeHeaderFileName(file.FileName)}\"";

        if (lastModified is not null)
            headers.LastModified = lastModified.Value.ToString("R");
    }

    /// <summary>
    /// Проверяет conditional request headers и возвращает true, если можно отдать 304.
    /// </summary>
    private static bool ClientCacheIsFresh(
        HttpContext httpContext,
        string etag,
        DateTime? lastModified)
    {
        var requestHeaders = httpContext.Request.Headers;

        if (requestHeaders.IfNoneMatch.Any(x =>
                x?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(tag => string.Equals(tag, etag, StringComparison.Ordinal)) == true))
        {
            return true;
        }

        if (lastModified is null)
            return false;

        if (!DateTimeOffset.TryParse(requestHeaders.IfModifiedSince, out var ifModifiedSince))
            return false;

        return lastModified.Value <= ifModifiedSince.UtcDateTime;
    }

    /// <summary>
    /// Нормализует имя файла для HTTP header Content-Disposition.
    /// </summary>
    private static string NormalizeHeaderFileName(string? fileName)
    {
        fileName = string.IsNullOrWhiteSpace(fileName)
            ? "file"
            : fileName.Trim();

        return fileName.Replace("\\", "_").Replace("/", "_").Replace("\"", "'");
    }
}
