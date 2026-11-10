using TechMES.Application.Equipment;
using TechMES.Application.Messages;
using TechMES.Application.Param;
using TechMES.Application.Scada;
using TechMES.Application.Runtime;
using TechMES.Calc.Abstractions;
using TechMES.Contracts.Security;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.CtApi;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.PostgreSql.Runtime;
using TechMES.Infrastructure.PostgreSql;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Runtime.Service.Endpoints;
using TechMES.Runtime.Service.Equipment;
using TechMES.Runtime.Service.Logging;
using TechMES.Runtime.Service.Messages;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;
using TechMES.Runtime.Service.Workers;
using TechMES.Runtime.Service.Calc;
using TechMES.Runtime.Service.PostgreSql;
using TechMES.PostgreSql.Control;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.PostgreSqlControl.json"), optional: true, reloadOnChange: false);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TechMES.Runtime.Service";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddSimpleFile(builder.Configuration);

// Настройки и единый источник активности Runtime.
builder.Services.AddOptions<RuntimeOptions>()
    .Bind(builder.Configuration.GetSection("Runtime"))
    .Validate(value => !value.Redundancy.Enabled || value.Redundancy.Role is "Primary" or "Secondary", "Runtime:Redundancy:Role must be Primary or Secondary.")
    .Validate(value => !value.Redundancy.Enabled || !string.IsNullOrWhiteSpace(value.Redundancy.Scope) && value.Redundancy.Scope.Length <= 100, "Runtime:Redundancy:Scope must contain between 1 and 100 characters.")
    .Validate(value => value.Redundancy.FailureThreshold is >= 1 and <= 10 && value.Redundancy.RecoveryThreshold is >= 1 and <= 10, "Runtime redundancy thresholds must be between 1 and 10.")
    .Validate(value => !value.Redundancy.Enabled || string.Equals(builder.Configuration["Database:Provider"] ?? "PostgreSql", "PostgreSql", StringComparison.OrdinalIgnoreCase), "Automatic Runtime coordination requires Database:Provider = PostgreSql.")
    .Validate(value => !value.Redundancy.Enabled || (builder.Configuration.GetValue<int?>("CtApi:HealthCheckPeriodSeconds") ?? 10) is >= 1 and <= 3600, "CtApi Health Period must be between 1 and 3600 seconds for Runtime coordination.")
    .ValidateOnStart();

builder.Services.AddSingleton<RuntimeActivityState>();
builder.Services.AddSingleton<IPlantScadaWritePermission>(provider => provider.GetRequiredService<RuntimeActivityState>());

// Диагностика физического кластера доступна независимо от Runtime Active/Standby.
builder.Services.AddSingleton<IPostgreSqlClusterProbe, PostgreSqlClusterProbe>();
builder.Services.AddSingleton<PostgreSqlClusterMonitor>();

// Общий локальный модуль PostgreSQL работает также в режиме Standby.
builder.Services.AddPostgreSqlNode(builder.Configuration, PostgreSqlHostKind.Runtime);
builder.Services.AddSingleton<PostgreSqlLocalMonitor>();

// Координатор получает разрешение, а activation worker готовит рабочие модули.
builder.Services.AddSingleton<IRuntimeLeadershipStore, PostgreSqlRuntimeLeadershipStore>();
builder.Services.AddHostedService<RuntimeCoordinationWorker>();
builder.Services.AddHostedService<RuntimeActivationWorker>();

// Существующие настройки модулей.
builder.Services.Configure<MessagesOptions>(builder.Configuration.GetSection("Messages"));
builder.Services.Configure<SoeOptions>(builder.Configuration.GetSection("Soe"));
builder.Services.Configure<ParamWriteOptions>(builder.Configuration.GetSection("ParamWrites"));
builder.Services.Configure<RoleGroupOptions>(builder.Configuration.GetSection(RoleGroupOptions.SectionName));
builder.Services.Configure<EquipmentCatalogOptions>(builder.Configuration.GetSection("EquipmentCatalog"));
builder.Services.Configure<CalcConfigurationOptions>(builder.Configuration.GetSection("CalcConfiguration"));

// Существующий lease Calc внутри активного Runtime.
builder.Services.AddOptions<CalcServiceMonitorOptions>()
    .Bind(builder.Configuration.GetSection("CalcServiceMonitor"))
    .Validate(options => options.OfflineAfterSeconds is >= 3 and <= 300, "CalcServiceMonitor:OfflineAfterSeconds must be between 3 and 300.")
    .Validate(options => options.LeaseDurationSeconds is >= 3 and <= 120, "CalcServiceMonitor:LeaseDurationSeconds must be between 3 and 120.")
    .Validate(options => options.OfflineAfterSeconds >= options.LeaseDurationSeconds, "CalcServiceMonitor:OfflineAfterSeconds must be greater than or equal to LeaseDurationSeconds.")
    .ValidateOnStart();

builder.Services.AddOptions<CalcWriteOptions>().Bind(builder.Configuration.GetSection(CalcWriteOptions.SectionName));

builder.Services.AddSingleton<CalcWriteDiagnosticsRegistry>();
builder.Services.AddSingleton<CalcOutputWriteCoordinator>();
builder.Services.AddSingleton<IAppRuntimeContext, AppRuntimeContext>();

builder.Services.AddSingleton(_ => BuiltInCalculationCatalog.Create());
builder.Services.AddSingleton<CalcJobValidator>();
builder.Services.AddSingleton<CalcDependencyGraphValidator>();
builder.Services.AddSingleton<CalcServiceHeartbeatRegistry>();

builder.Services.AddSignalR();

// Фоновые задачи учитывают текущее состояние активности.
builder.Services.AddHostedService<MessageStorageWatcher>();
builder.Services.AddHostedService<PlantScadaHealthWorker>();

// Сначала существующий adapter, затем защита поверх него.
builder.Services.AddCtApiInfrastructure(builder.Configuration);
builder.Services.AddRuntimeActivityProtection();

var messageStorageProvider = builder.Configuration["MessageStorage:Provider"] ?? "InMemory";

if (string.Equals(messageStorageProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMessageStore, InMemoryMessageStore>();
}
else if (string.Equals(messageStorageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddPostgreSqlInfrastructure(builder.Configuration);
}
else
{
    throw new InvalidOperationException($"Неизвестный MessageStorage:Provider = '{messageStorageProvider}'. Поддерживаются значения: InMemory, PostgreSql.");
}

builder.Services.AddPostgreSqlInfoInfrastructure(builder.Configuration);
builder.Services.AddPostgreSqlEventLogInfrastructure(builder.Configuration);
builder.Services.AddRecipeInfrastructure();

var equipmentCatalogProvider = builder.Configuration["EquipmentCatalog:Provider"] ?? "InMemory";

if (string.Equals(equipmentCatalogProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEquipmentCatalogProvider, InMemoryEquipmentCatalogProvider>();
}
else if (string.Equals(equipmentCatalogProvider, "CtApi", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEquipmentCatalogProvider, CtApiEquipmentCatalogProvider>();
}
else
{
    throw new InvalidOperationException($"Неизвестный EquipmentCatalog:Provider = '{equipmentCatalogProvider}'. Поддерживаются значения: InMemory, CtApi.");
}

var app = builder.Build();

app.UseHttpsRedirection();

// Маршрут должен быть определён до проверки его служебной метки.
app.UseRouting();
app.UseMiddleware<RuntimeActivityMiddleware>();

// Рабочие зависимости готовит RuntimeActivationWorker.
app.MapRuntimeEndpoints();
app.MapUserStateEndpoints();

// Готовый снимок: HTTP-запрос не открывает соединения к PostgreSQL.
app.MapGet("/api/database/cluster", (PostgreSqlClusterMonitor cluster) => Results.Ok(cluster.GetHealth())).WithMetadata(new RuntimeControlEndpoint());

// Локальная диагностика прав и принадлежности процесса службе.
app.MapPostgreSqlControlEndpoints();

app.Run();