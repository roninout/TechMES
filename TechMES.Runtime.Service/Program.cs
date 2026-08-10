using TechMES.Application.Equipment;
using TechMES.Application.Messages;
using TechMES.Application.Param;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.PostgreSql;
using TechMES.Runtime.Service.Endpoints;
using TechMES.Runtime.Service.Equipment;
using TechMES.Runtime.Service.Logging;
using TechMES.Runtime.Service.Messages;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;
using TechMES.Runtime.Service.Workers;
using TechMES.Calc.Abstractions;
using TechMES.Runtime.Service.Calc;

var builder = WebApplication.CreateBuilder(args);

// Runtime.Service рассчитан на запуск как Windows Service на промышленном сервере.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TechMES.Runtime.Service";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddSimpleFile(builder.Configuration);

// Runtime Service — будущий Windows Service.
// Сейчас запускаем его как обычный Web API, чтобы удобно тестировать WEB ↔ Service.

// Настройки Runtime.Service из appsettings.json.
builder.Services.Configure<RuntimeOptions>(builder.Configuration.GetSection("Runtime"));

// Настройки модуля Messages. Здесь задаётся период фоновой проверки БД на внешние изменения.
builder.Services.Configure<MessagesOptions>(builder.Configuration.GetSection("Messages"));
builder.Services.Configure<SoeOptions>(builder.Configuration.GetSection("Soe"));
builder.Services.Configure<ParamWriteOptions>(builder.Configuration.GetSection("ParamWrites"));

// Настройки каталога оборудования. Provider выбирается из appsettings.json: InMemory или CtApi.
builder.Services.Configure<EquipmentCatalogOptions>(builder.Configuration.GetSection("EquipmentCatalog"));

// Настройки API редактирования расчётных заданий.
builder.Services.Configure<CalcConfigurationOptions>(builder.Configuration.GetSection("CalcConfiguration"));

// Calc.Service должен регулярно подтверждать Runtime, что процесс ещё работает.
builder.Services.AddOptions<CalcServiceMonitorOptions>()
    .Bind(builder.Configuration.GetSection("CalcServiceMonitor"))
    .Validate(options => options.OfflineAfterSeconds is >= 3 and <= 300, "CalcServiceMonitor:OfflineAfterSeconds must be between 3 and 300.")
    .ValidateOnStart();

// Runtime-контекст регистрируем как Singleton, потому что имя устройства/версия не меняются во время работы процесса.
builder.Services.AddSingleton<IAppRuntimeContext, AppRuntimeContext>();

// Каталог содержит только встроенные расчётные алгоритмы и не обращается к PostgreSQL/CtApi.
builder.Services.AddSingleton(_ => BuiltInCalculationCatalog.Create());

// Валидатор проверяет задания по реальному каталогу алгоритмов.
builder.Services.AddSingleton<CalcJobValidator>();

// Последний heartbeat Calc.Service хранится только в памяти Runtime.
builder.Services.AddSingleton<CalcServiceHeartbeatRegistry>();

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

// Info-модуль использует существующие PostgreSQL-таблицы из WPF БД.
builder.Services.AddPostgreSqlInfoInfrastructure(builder.Configuration);

// Operation actions и Alarm history читают существующую EventPicker/PostgreSQL БД.
builder.Services.AddPostgreSqlEventLogInfrastructure(builder.Configuration);

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
    /*
        Инициализируем основную PostgreSQL-БД до старта модулей Runtime.

        Почему это нужно делать здесь:
        - MessageStore умеет создавать только свои таблицы Messages.
        - Info/Favorites/Orders/Notes раньше ожидали уже готовую БД.
        - На новом сервере /api/equipment падал с ошибкой:
          relation "public.equip_favorite" does not exist.

        Теперь Runtime сам:
        1. создает Database из Database:ConnectionString, если ее нет;
        2. создает недостающие таблицы Info/Favorites/Orders/Notes/ParamTune;
        3. после этого запускает Messages, CtApi и Equipment catalog.
    */
    var databaseBootstrapper = scope.ServiceProvider.GetRequiredService<PostgreSqlDatabaseBootstrapper>();
    await databaseBootstrapper.InitializeMainDatabaseAsync();

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

app.MapRuntimeEndpoints();

app.Run();
