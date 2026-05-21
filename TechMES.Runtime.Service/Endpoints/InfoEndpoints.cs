using TechMES.Application.Info;
using TechMES.Contracts.Info;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

public static class InfoEndpoints
{
    public static IEndpointRouteBuilder MapInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info/{equipName}", GetInfoAsync);
        app.MapPut("/api/info/{equipName}/description", SaveDescriptionAsync);
        app.MapPost("/api/info/{equipName}/notes", AddNoteAsync);
        app.MapPut("/api/info/{equipName}/notes/{noteId:long}", UpdateNoteAsync);
        app.MapDelete("/api/info/{equipName}/notes/{noteId:long}", DeleteNoteAsync);

        return app;
    }

    private static async Task<IResult> GetInfoAsync(
        string equipName,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        var info = await infoStore.GetAsync(equipName, ct);
        return Results.Ok(info);
    }

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

    private static async Task<IResult> DeleteNoteAsync(
        string equipName,
        long noteId,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        await infoStore.DeleteNoteAsync(equipName, noteId, ct);
        return Results.Ok();
    }

    private static string ResolveActorName(string? deviceName, IAppRuntimeContext runtime)
    {
        return string.IsNullOrWhiteSpace(deviceName)
            ? runtime.DeviceName
            : deviceName.Trim();
    }
}
