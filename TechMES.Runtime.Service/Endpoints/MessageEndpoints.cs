using Microsoft.AspNetCore.SignalR;
using TechMES.Application.Messages;
using TechMES.Contracts.Messages;
using TechMES.Runtime.Service.Hubs;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        // SignalR endpoint. WEB подключается сюда для live-уведомлений.
        app.MapHub<MessagesHub>("/hubs/messages");

        app.MapGet("/api/messages", GetMessagesAsync);
        app.MapPost("/api/messages", SaveMessageAsync);
        app.MapPost("/api/messages/{id:long}/viewed", MarkViewedAsync);
        app.MapPost("/api/messages/{id:long}/toggle-active", ToggleActiveAsync);
        app.MapDelete("/api/messages/{id:long}", DeleteMessageAsync);

        return app;
    }

    private static async Task<IResult> GetMessagesAsync(
        bool includeInactive,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        CancellationToken ct)
    {
        // WEB вызывает этот endpoint при загрузке страницы Messages.
        // Runtime Service получает данные через IMessageStore,
        // не зная, какой adapter подключён физически.
        var actorName = ResolveActorName(deviceName, runtime);

        var messages = await messageStore.GetMessagesAsync(
            includeInactive,
            actorName,
            ct);

        var activeCount = await messageStore.GetActiveMessageCountAsync(ct);

        return Results.Ok(new MessageListResponse
        {
            Messages = messages,
            ActiveMessageCount = activeCount
        });
    }

    private static async Task<IResult> SaveMessageAsync(
        SaveMessageRequest request,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
        // WEB отправляет сюда SaveMessageRequest.
        // Runtime Service сам определяет служебные поля: CreatedBy, CreatedAt, UpdatedBy.
        var actorName = ResolveActorName(deviceName, runtime);

        if (string.IsNullOrWhiteSpace(request.MessageSubject))
            return Results.BadRequest("Message subject is required.");

        if (string.IsNullOrWhiteSpace(request.MessageText))
            return Results.BadRequest("Message text is required.");

        var isNew = request.Id <= 0;

        var saved = await messageStore.SaveMessageAsync(
            request,
            userName: actorName,
            deviceName: actorName,
            ct);

        await NotifyMessagesChangedAsync(
            hubContext,
            isNew ? MessageChangedEventType.Created : MessageChangedEventType.Updated,
            saved.Id,
            actorName,
            ct);

        return Results.Ok(saved);
    }

    private static async Task<IResult> MarkViewedAsync(
        long id,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
        // WEB вызывает этот endpoint после того, как пользователь удержал сообщение выбранным.
        var actorName = ResolveActorName(deviceName, runtime);

        await messageStore.MarkViewedAsync(
            id,
            actorName,
            ct);

        await NotifyMessagesChangedAsync(
            hubContext,
            MessageChangedEventType.Viewed,
            id,
            actorName,
            ct);

        return Results.Ok();
    }

    private static async Task<IResult> ToggleActiveAsync(
        long id,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
        // Переключаем Active / Inactive.
        // Сейчас бизнес-правило простое: менять активность может только автор.
        var actorName = ResolveActorName(deviceName, runtime);

        var ok = await messageStore.ToggleActivityAsync(
            id,
            actorName,
            ct);

        if (!ok)
            return Results.BadRequest("Only message author can change activity.");

        await NotifyMessagesChangedAsync(
            hubContext,
            MessageChangedEventType.ActivityChanged,
            id,
            actorName,
            ct);

        return Results.Ok();
    }

    private static async Task<IResult> DeleteMessageAsync(
        long id,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
        var actorName = ResolveActorName(deviceName, runtime);

        await messageStore.DeleteMessageAsync(id, ct);

        await NotifyMessagesChangedAsync(
            hubContext,
            MessageChangedEventType.Deleted,
            id,
            actorName,
            ct);

        return Results.Ok();
    }

    private static string ResolveActorName(string? deviceName, IAppRuntimeContext runtime)
    {
        // Если WEB передал deviceName — используем его.
        // Если не передал — используем имя Runtime.Service.
        return string.IsNullOrWhiteSpace(deviceName)
            ? runtime.DeviceName
            : deviceName.Trim();
    }

    private static Task NotifyMessagesChangedAsync(
        IHubContext<MessagesHub> hubContext,
        MessageChangedEventType eventType,
        long? messageId,
        string changedBy,
        CancellationToken ct)
    {
        var notification = new MessageChangedNotification
        {
            EventType = eventType,
            MessageId = messageId,
            ChangedBy = changedBy,
            ChangedAt = DateTime.Now
        };

        return hubContext.Clients.All.SendAsync("MessagesChanged", notification, ct);
    }
}
