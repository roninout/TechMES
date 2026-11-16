using Microsoft.AspNetCore.Server.Kestrel.Https;
using TechMES.Contracts.Host;
using TechMES.Host.Control;
using System.Net;
using TechMES.Host;

// Windows-служба читает настройки рядом с exe независимо от текущего каталога SCM.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(options => options.ServiceName = "TechMES.Host");
var settings = builder.Configuration.GetSection("HostService").Get<HostServiceOptions>() ?? new();
settings.Validate();
var shared = builder.Configuration.GetSection("HostSharedAddress").Get<HostSharedAddressSettings>() ?? new();
var control = builder.Configuration.GetSection("HostControl").Get<HostControlSettings>() ?? new();
control.Validate(settings.Port);
var nodeCertificate = control.Enabled ? HostNodeCertificates.LoadLocal(control.CertificateSha256, shared.NodeAddress) : null;
if (control.Enabled && (!HostNetworkPolicy.TryAddress(shared.NodeAddress, out _) || !HostNetworkPolicy.TryAddress(shared.PeerAddress, out _) || shared.NodeAddress == shared.PeerAddress)) throw new InvalidOperationException("HostControl requires different IPv4 node addresses.");

// Локальная диагностика всегда доступна; сетевой endpoint включается отдельно и требует mTLS.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024;
    options.Listen(IPAddress.Loopback, settings.Port);
    if (control.Enabled) options.Listen(IPAddress.Parse(shared.NodeAddress), control.Port, endpoint => endpoint.UseHttps(https =>
    {
        https.ServerCertificate = nodeCertificate;
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (certificate, _, _) => HostNodeCertificates.AcceptClient(certificate, control);
    }));
});
builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient("WebProbe", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
builder.Services.AddSingleton(sp => new HostMonitor(sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebProbe"), settings, sp.GetRequiredService<ILogger<HostMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMonitor>());

var app = builder.Build();
// Проверяем сертификат и на уровне запроса: защищает также HTTP/2 соединения и будущие маршруты управления.
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == control.Port && control.Enabled && (!context.Request.IsHttps || !HostNodeCertificates.AcceptClient(await context.Connection.GetClientCertificateAsync(), control)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
var startedAtUtc = DateTimeOffset.UtcNow;
var instanceId = Guid.NewGuid().ToString("N");
app.Lifetime.ApplicationStopped.Register(() => nodeCertificate?.Dispose());

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
// Все новые маршруты только читают сеть. Ни один из них не добавляет и не удаляет IP.
app.MapGet("/api/host/network", () => Results.Ok(HostNetworkInspector.Read()));
app.MapPost("/api/host/network/check", (HostSharedAddressSettings request) => Results.Ok(HostNetworkPolicy.Check(request, HostNetworkInspector.Read())));
app.MapGet("/api/host/shared-address/status", () => Results.Ok(HostNetworkPolicy.Check(shared, HostNetworkInspector.Read())));
app.Run();
