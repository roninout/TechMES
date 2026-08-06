using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Service;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TechMES.Calc.Service";
});

/*
 * Calc.Service знает только HTTP-адрес Runtime.Service.
 * PostgreSQL connection string и CtApi-настройки сюда не передаются.
 */
builder.Services.AddOptions<CalcRuntimeClientOptions>()
    .Bind(builder.Configuration.GetSection("Runtime"))
    .Validate(options =>
    {
        return Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }, "Runtime:BaseAddress must contain a valid HTTP or HTTPS address.")
    .Validate(options => options.RequestTimeoutSeconds is > 0 and <= 300,
        "Runtime:RequestTimeoutSeconds must be between 1 and 300.")
    .Validate(options => options.ConfigurationRefreshSeconds is > 0 and <= 3600,
        "Runtime:ConfigurationRefreshSeconds must be between 1 and 3600.")
    .ValidateOnStart();

/*
 * Локальный каталог нужен для проверки совместимости Runtime
 * и установленной версии TechMES.Calc.Service.
 */
builder.Services.AddSingleton(_ => BuiltInCalculationCatalog.Create());

builder.Services.AddHttpClient<IRuntimeCalcClient, RuntimeCalcClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<CalcRuntimeClientOptions>>().Value;
    var baseAddress = options.BaseAddress.Trim().TrimEnd('/') + "/";

    client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

builder.Services.AddHostedService<CalcWorker>();

await builder.Build().RunAsync();