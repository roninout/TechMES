using Microsoft.AspNetCore.SignalR;
using TechMES.Application.Messages;
using TechMES.Contracts.Messages;
using TechMES.Runtime.Service.Hubs;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP и SignalR API модуля Messages.
/// Runtime.Service инкапсулирует IMessageStore, поэтому WEB не знает,
/// используется InMemory-адаптер или PostgreSQL.
/// </summary>
public static class MessageEndpoints
{
    /// <summary>
    /// Подключает REST endpoints сообщений и SignalR hub live-уведомлений.
    /// </summary>
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHub<MessagesHub>("/hubs/messages");

        app.MapGet("/api/messages", GetMessagesAsync);
        app.MapPost("/api/messages", SaveMessageAsync);
        app.MapPost("/api/messages/{id:long}/viewed", MarkViewedAsync);
        app.MapPost("/api/messages/{id:long}/toggle-active", ToggleActiveAsync);
        app.MapDelete("/api/messages/{id:long}", DeleteMessageAsync);

        return app;
    }

    /// <summary>
    /// Возвращает список сообщений и счетчик активных сообщений для footer/menu.
    /// deviceName определяет, какие сообщения уже просмотрены текущим клиентом.
    /// </summary>
    private static async Task<IResult> GetMessagesAsync(
        bool includeInactive,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        CancellationToken ct)
    {
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

    /// <summary>
    /// Создает или обновляет сообщение.
    /// Runtime.Service сам заполняет служебные поля автора/обновления через IMessageStore.
    /// </summary>
    private static async Task<IResult> SaveMessageAsync(
        SaveMessageRequest request,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
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

    /// <summary>
    /// Отмечает сообщение просмотренным для текущего устройства/клиента.
    /// После изменения рассылается SignalR-уведомление всем WEB-сессиям.
    /// </summary>
    private static async Task<IResult> MarkViewedAsync(
        long id,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
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

    /// <summary>
    /// Переключает active/inactive. Правило хранится в IMessageStore:
    /// сейчас менять активность может только автор сообщения.
    /// </summary>
    private static async Task<IResult> ToggleActiveAsync(
        long id,
        string? deviceName,
        IMessageStore messageStore,
        IAppRuntimeContext runtime,
        IHubContext<MessagesHub> hubContext,
        CancellationToken ct)
    {
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

    /// <summary>
    /// Удаляет сообщение и рассылает событие обновления.
    /// </summary>
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

    /// <summary>
    /// Определяет имя клиента для user/device полей.
    /// Если WEB не передал deviceName, используем Runtime.DeviceName.
    /// </summary>
    private static string ResolveActorName(string? deviceName, IAppRuntimeContext runtime)
    {
        return string.IsNullOrWhiteSpace(deviceName)
            ? runtime.DeviceName
            : deviceName.Trim();
    }

    /// <summary>
    /// Рассылает всем WEB-клиентам событие изменения сообщений.
    /// Клиенты после события сами перечитывают актуальное состояние.
    /// </summary>
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
