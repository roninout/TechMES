using Microsoft.AspNetCore.SignalR;
using TechMES.Application.Equipment;
using TechMES.Application.Messages;
using TechMES.Application.Scada;
using TechMES.Contracts.Messages;
using TechMES.Contracts.Runtime;
using TechMES.Contracts.Scada;
using TechMES.Infrastructure.CtApi;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.PostgreSql;
using TechMES.Runtime.Service.Equipment;
using TechMES.Runtime.Service.Hubs;
using TechMES.Runtime.Service.Messages;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;
using TechMES.Runtime.Service.Workers;


var builder = WebApplication.CreateBuilder(args);

// Runtime Service — будущий Windows Service.
// Сейчас запускаем его как обычный Web API, чтобы удобно тестировать WEB ↔ Service.

// Настройки Runtime.Service из appsettings.json.
builder.Services.Configure<RuntimeOptions>(builder.Configuration.GetSection("Runtime"));

// Настройки модуля Messages.
// Здесь задаётся период фоновой проверки БД на внешние изменения.
builder.Services.Configure<MessagesOptions>(builder.Configuration.GetSection("Messages"));

// Настройки каталога оборудования.
// Provider выбирается из appsettings.json: InMemory или CtApi.
builder.Services.Configure<EquipmentCatalogOptions>(builder.Configuration.GetSection("EquipmentCatalog"));

// Runtime-контекст регистрируем как Singleton,
// потому что имя устройства/версия не меняются во время работы процесса.
builder.Services.AddSingleton<IAppRuntimeContext, AppRuntimeContext>();

// SignalR нужен для live-обновлений.
// Например: один клиент создал сообщение, остальные клиенты сразу получили событие.
builder.Services.AddSignalR();

// Фоновый watcher проверяет, не изменились ли сообщения напрямую в БД.
// Если изменились — отправляет SignalR-событие WEB-клиентам.
builder.Services.AddHostedService<MessageStorageWatcher>();

// Фоновый монитор Plant SCADA adapter-а.
// Пока он только логирует состояние.
// Позже будет выполнять ReadTag health-check и reconnect.
builder.Services.AddHostedService<PlantScadaHealthWorker>();

// Plant SCADA / CtApi adapter.
// Provider выбирается из appsettings.json: Disabled, Mock или CtApi.
builder.Services.AddCtApiInfrastructure(builder.Configuration);

// Подключаем хранилище сообщений через adapter-подход.
// Runtime.Service работает только с IMessageStore.
// Конкретная реализация выбирается из appsettings.json: MessageStorage:Provider.
var messageStorageProvider = builder.Configuration["MessageStorage:Provider"] ?? "InMemory";

if (string.Equals(messageStorageProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    // Временный адаптер для разработки.
    // Данные живут только пока запущен Runtime Service.
    builder.Services.AddSingleton<IMessageStore, InMemoryMessageStore>();
}
else if (string.Equals(messageStorageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    // PostgreSQL-адаптер.
    // SQL-запросы находятся внутри TechMES.Infrastructure.PostgreSql.
    builder.Services.AddPostgreSqlInfrastructure(builder.Configuration);
}
else
{
    throw new InvalidOperationException($"Неизвестный MessageStorage:Provider = '{messageStorageProvider}'. " + "Поддерживаются значения: InMemory, PostgreSql.");
}

// Каталог оборудования тоже подключаем через adapter-подход.
// Runtime.Service работает с IEquipmentCatalogProvider,
// а конкретный provider выбирается из appsettings.json.
var equipmentCatalogProvider = builder.Configuration["EquipmentCatalog:Provider"] ?? "InMemory";

if (string.Equals(equipmentCatalogProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEquipmentCatalogProvider, InMemoryEquipmentCatalogProvider>();
}
else if (string.Equals(equipmentCatalogProvider, "CtApi", StringComparison.OrdinalIgnoreCase))
{
    /*
        Реальный каталог оборудования через CtApi.

        Важно:
        CtApiEquipmentCatalogProvider использует тот же ICtApiNativeClient,
        что и CtApiPlantScadaGateway. Поэтому он работает через уже открытый CtApi.
    */
    builder.Services.AddSingleton<IEquipmentCatalogProvider, CtApiEquipmentCatalogProvider>();
}
else
{
    throw new InvalidOperationException(
        $"Неизвестный EquipmentCatalog:Provider = '{equipmentCatalogProvider}'. " +
        "Поддерживаются значения: InMemory, CtApi.");
}

var app = builder.Build();

app.UseHttpsRedirection();

// Инициализируем выбранный адаптер сообщений.
// Для PostgreSQL здесь создаются таблицы equip_message/equip_message_view, если их нет.
using (var scope = app.Services.CreateScope())
{
    var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
    await messageStore.InitializeAsync();

    /*
        ВАЖНО:
        Plant SCADA / CtApi нужно инициализировать ДО каталога оборудования.

        Почему:
        CtApiEquipmentCatalogProvider получает список оборудования через ICtApiNativeClient.FindAsync().
        А FindAsync требует открытого CtApi connection.

        Поэтому порядок такой:
        1. Открываем CtApi.
        2. Потом загружаем Equipment catalog.
    */
    var plantScadaGateway = scope.ServiceProvider.GetRequiredService<IPlantScadaGateway>();
    await plantScadaGateway.InitializeAsync();

    var equipmentCatalog = scope.ServiceProvider.GetRequiredService<IEquipmentCatalogProvider>();
    await equipmentCatalog.InitializeAsync();
}

// SignalR endpoint.
// WEB будет подключаться сюда для live-уведомлений.
app.MapHub<MessagesHub>("/hubs/messages");

app.MapGet("/api/health", async (IConfiguration configuration, IAppRuntimeContext runtime, IMessageStore messageStore, CancellationToken ct) =>
{
    // Health endpoint нужен не только для проверки "процесс жив",
    // но и для WEB-интерфейса и будущего WPF Configurator.
    //
    // Здесь мы проверяем:
    // - Runtime.Service запущен;
    // - IMessageStore отвечает;
    // - PostgreSQL/adapter доступен;
    // - можно получить количество активных сообщений.
    try
    {
        var activeCount = await messageStore.GetActiveMessageCountAsync(ct);

        return Results.Ok(new RuntimeHealthResponse
        {
            Status = "OK",
            Service = "TechMES.Runtime.Service",
            DeviceName = runtime.DeviceName,
            UserName = runtime.UserName,
            MachineName = runtime.MachineName,
            AppVersion = runtime.AppVersion,
            MessageStorageProvider = configuration["MessageStorage:Provider"] ?? "InMemory",
            Database = "OK",
            ActiveMessageCount = activeCount,
            Time = DateTime.Now
        });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new RuntimeHealthResponse
            {
                Status = "ERROR",
                Service = "TechMES.Runtime.Service",
                DeviceName = runtime.DeviceName,
                UserName = runtime.UserName,
                MachineName = runtime.MachineName,
                AppVersion = runtime.AppVersion,
                MessageStorageProvider = configuration["MessageStorage:Provider"] ?? "InMemory",
                Database = "ERROR",
                ActiveMessageCount = 0,
                Time = DateTime.Now,
                Error = ex.Message
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/messages", async (bool includeInactive, string? deviceName, IMessageStore messageStore, IAppRuntimeContext runtime, CancellationToken ct) =>
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
});

app.MapPost("/api/messages", async (SaveMessageRequest request, string? deviceName, IMessageStore messageStore, IAppRuntimeContext runtime, IHubContext<MessagesHub> hubContext, CancellationToken ct) =>
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

    // После сохранения отправляем SignalR-событие всем открытым WEB-клиентам.
    await NotifyMessagesChangedAsync(
        hubContext,
        isNew ? MessageChangedEventType.Created : MessageChangedEventType.Updated,
        saved.Id,
        actorName,
        ct);

    return Results.Ok(saved);
});

app.MapPost("/api/messages/{id:long}/viewed", async (long id, string? deviceName, IMessageStore messageStore, IAppRuntimeContext runtime, IHubContext<MessagesHub> hubContext, CancellationToken ct) =>
{
    // WEB вызывает этот endpoint после того, как пользователь удержал сообщение выбранным.
    var actorName = ResolveActorName(deviceName, runtime);

    await messageStore.MarkViewedAsync(
        id,
        actorName,
        ct);

    // Viewed тоже отправляем через SignalR,
    // чтобы у других клиентов обновилось поле ViewedBy.
    await NotifyMessagesChangedAsync(
        hubContext,
        MessageChangedEventType.Viewed,
        id,
        actorName,
        ct);

    return Results.Ok();
});

app.MapPost("/api/messages/{id:long}/toggle-active", async (long id, string? deviceName, IMessageStore messageStore, IAppRuntimeContext runtime, IHubContext<MessagesHub> hubContext, CancellationToken ct) =>
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
});

app.MapDelete("/api/messages/{id:long}", async (long id, string? deviceName, IMessageStore messageStore, IAppRuntimeContext runtime, IHubContext<MessagesHub> hubContext, CancellationToken ct) =>
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
});

app.MapGet("/api/equipment", async (IEquipmentCatalogProvider equipmentCatalog, CancellationToken ct) =>
{
    // WEB вызывает этот endpoint, чтобы получить каталог оборудования.
    // Сейчас данные приходят из InMemory provider-а.
    // Позже этот же endpoint будет получать данные из CtApi provider-а.
    var response = await equipmentCatalog.GetEquipmentListAsync(ct);

    return Results.Ok(response);
});

app.MapGet("/api/equipment/{name}", async (string name, IEquipmentCatalogProvider equipmentCatalog, CancellationToken ct) =>
{
    // Получить одно оборудование по имени.
    // Имя может содержать точки: S01.H01.P01.
    var equipment = await equipmentCatalog.GetEquipmentByNameAsync(name, ct);

    return equipment is null
        ? Results.NotFound()
        : Results.Ok(equipment);
});

app.MapGet("/api/scada/health", async (
    IPlantScadaGateway plantScadaGateway,
    CancellationToken ct) =>
{
    // Проверка состояния Plant SCADA adapter-а.
    // WEB/Configurator будут использовать этот endpoint для диагностики.
    var health = await plantScadaGateway.GetHealthAsync(ct);

    return Results.Ok(health);
});

app.MapGet("/api/scada/tags/{tagName}", async (
    string tagName,
    IPlantScadaGateway plantScadaGateway,
    CancellationToken ct) =>
{
    // Чтение одного tag.
    // Работает через выбранный Plant SCADA adapter: Mock, Disabled или CtApi.
    var result = await plantScadaGateway.ReadTagAsync(tagName, ct);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

app.MapPost("/api/scada/tags/write", async (
    ScadaTagWriteRequest request,
    IPlantScadaGateway plantScadaGateway,
    CancellationToken ct) =>
{
    // Запись одного tag.
    // В реальном CtApi adapter-е здесь позже добавим:
    // - проверку AllowWrites;
    // - проверку прав;
    // - operator action log;
    // - нормализацию bool/int/string значений.
    var result = await plantScadaGateway.WriteTagAsync(request, ct);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

app.Run();

static string ResolveActorName(string? deviceName, IAppRuntimeContext runtime)
{
    // Если WEB передал deviceName — используем его.
    // Если не передал — используем имя Runtime.Service.
    return string.IsNullOrWhiteSpace(deviceName)
        ? runtime.DeviceName
        : deviceName.Trim();
}

static Task NotifyMessagesChangedAsync(IHubContext<MessagesHub> hubContext, MessageChangedEventType eventType, long? messageId, string changedBy, CancellationToken ct)
{
    var notification = new MessageChangedNotification
    {
        EventType = eventType,
        MessageId = messageId,
        ChangedBy = changedBy,
        ChangedAt = DateTime.Now
    };

    // Имя события "MessagesChanged" должен знать WEB-клиент.
    return hubContext.Clients.All.SendAsync("MessagesChanged", notification, ct);
}