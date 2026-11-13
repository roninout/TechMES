using System.Net;
using TechMES.Host;

// Windows-служба читает настройки рядом с exe независимо от текущего каталога SCM.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(options => options.ServiceName = "TechMES.Host");
var settings = builder.Configuration.GetSection("HostService").Get<HostServiceOptions>() ?? new();
settings.Validate();

// Первый этап предоставляет только локальную диагностику. Внешний API пока не открываем.
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, settings.Port));
builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient("WebProbe", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
builder.Services.AddSingleton(sp => new HostMonitor(sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebProbe"), settings, sp.GetRequiredService<ILogger<HostMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMonitor>());

var app = builder.Build();
var startedAtUtc = DateTimeOffset.UtcNow;
var instanceId = Guid.NewGuid().ToString("N");

// Liveness Host не зависит от WEB: неисправный WEB не означает, что контроллер остановлен.
app.MapGet("/api/health", () => Results.Ok(new { Service = "TechMES.Host", Status = "OK", Mode = "Observer", SharedAddressEnabled = false }));
app.MapGet("/api/host/status", (HostMonitor monitor) => Results.Ok(new
{
    Service = "TechMES.Host", DisplayName = "TechMes Host", Computer = Environment.MachineName,
    InstanceId = instanceId, StartedAtUtc = startedAtUtc, ProcessId = Environment.ProcessId,
    Version = typeof(HostMonitor).Assembly.GetName().Version?.ToString(),
    Mode = "Observer", SharedAddressEnabled = false, WebReady = monitor.IsWebReady,
    WebHealthUrl = settings.WebHealthUrl, Web = monitor.Status
}));
app.MapGet("/api/health/ready", (HostMonitor monitor) => Results.Json(new { Service = "TechMES.Host", WebReady = monitor.IsWebReady, Web = monitor.Status }, statusCode: monitor.IsWebReady ? 200 : 503));
app.Run();
