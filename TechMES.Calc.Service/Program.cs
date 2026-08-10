using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Service;
using TechMES.Calc.Service.Execution;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TechMES.Calc.Service";
});

// Calc.Service знает только HTTP-адрес Runtime.Service.
// PostgreSQL connection string и CtApi-настройки сюда не передаются.
builder.Services.AddOptions<CalcRuntimeClientOptions>()
    .Bind(builder.Configuration.GetSection("Runtime"))
    .Validate(options => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "Runtime:BaseAddress must contain a valid HTTP or HTTPS address.")
    .Validate(options => options.RequestTimeoutSeconds is > 0 and <= 300,
        "Runtime:RequestTimeoutSeconds must be between 1 and 300.")
    .Validate(options => options.ConfigurationRefreshSeconds is > 0 and <= 3600,
        "Runtime:ConfigurationRefreshSeconds must be between 1 and 3600.")
    .Validate(options => options.HeartbeatSeconds is >= 1 and <= 60,
        "Runtime:HeartbeatSeconds must be between 1 and 60.")
    .ValidateOnStart();

// Настройки scheduler-а и обработки качества входных значений.
builder.Services.AddOptions<CalcExecutionOptions>()
    .Bind(builder.Configuration.GetSection("Execution"))
    .Validate(options => options.SchedulerTickMilliseconds is >= 50 and <= 5000,
        "Execution:SchedulerTickMilliseconds must be between 50 and 5000.")
    .Validate(options => options.DefaultMaxAgeSeconds is > 0 and <= 86400,
        "Execution:DefaultMaxAgeSeconds must be between 1 and 86400.")
    .ValidateOnStart();

// Один InstanceId используется heartbeat и результатами расчётов.
builder.Services.AddSingleton<CalcServiceIdentity>();

// Единое локальное состояние ownership используется heartbeat-worker и scheduler-ом.
builder.Services.AddSingleton<CalcServiceLeaseState>();

// Локальный каталог нужен для проверки совместимости Runtime и Calc.Service.
builder.Services.AddSingleton(_ => BuiltInCalculationCatalog.Create());

builder.Services.AddHttpClient<IRuntimeCalcClient, RuntimeCalcClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<CalcRuntimeClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

// Движок выполняет только shadow-расчёты и не имеет прямого доступа к CtApi/PostgreSQL.
builder.Services.AddSingleton<CalcExecutionEngine>();

// Heartbeat работает независимо от scheduler-а расчётов.
builder.Services.AddHostedService<CalcHeartbeatWorker>();
builder.Services.AddHostedService<CalcWorker>();

await builder.Build().RunAsync();