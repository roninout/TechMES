using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net;
using TechMES.Contracts.Host;
using TechMES.Host;
using TechMES.Host.Control;

// Windows-служба читает настройки рядом с exe независимо от текущего каталога SCM.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Host.UseWindowsService(options => options.ServiceName = "TechMES.Host");

var settings = builder.Configuration.GetSection("HostService").Get<HostServiceOptions>() ?? new();
settings.Validate();

var shared = builder.Configuration.GetSection("HostSharedAddress").Get<HostSharedAddressSettings>() ?? new();
var control = builder.Configuration.GetSection("HostControl").Get<HostControlSettings>() ?? new();

control.Validate(settings.Port);

var nodeCertificate = control.Enabled
    ? HostNodeCertificates.LoadLocal(control.CertificateSha256, shared.NodeAddress)
    : null;

if (control.Enabled && (!HostNetworkPolicy.TryAddress(shared.NodeAddress, out _) || !HostNetworkPolicy.TryAddress(shared.PeerAddress, out _) || shared.NodeAddress == shared.PeerAddress))
    throw new InvalidOperationException("HostControl requires different IPv4 node addresses.");

// Локальная диагностика всегда доступна; сетевой endpoint требует mTLS.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024;
    options.Listen(IPAddress.Loopback, settings.Port);

    if (control.Enabled)
    {
        options.Listen(IPAddress.Parse(shared.NodeAddress), control.Port, endpoint => endpoint.UseHttps(https =>
        {
            https.ServerCertificate = nodeCertificate;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => HostNodeCertificates.AcceptClient(certificate, control);
        }));
    }
});

builder.Services.AddSingleton(settings);

builder.Services.AddHttpClient("WebProbe", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    });

builder.Services.AddSingleton(sp => new HostMonitor(sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebProbe"), settings, sp.GetRequiredService<ILogger<HostMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMonitor>());

// Файлы ownership живут в ProgramData и не заменяются пакетом обновления.
HostOwnerEngine? owner = null;
HostOwnerStore? ownerStore = null;

if (control.Enabled)
{
    var pair = HostOwnerIdentity.Pair(shared, control);

    var store = ownerStore = new HostOwnerStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "Host"),
        pair,
        shared.InterfaceId);

    builder.Services.AddSingleton(sp => owner = new HostOwnerEngine(shared, pair, store, new HostVipWindows(shared), new HostOwnerTransport(shared, control), () => sp.GetRequiredService<HostMonitor>().IsWebReady));
    builder.Services.AddHostedService<HostOwnerWorker>();
}

builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));

var app = builder.Build();

if (control.Enabled)
    owner = app.Services.GetRequiredService<HostOwnerEngine>();

// Проверка сертификата выполняется также на уровне запроса.
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == control.Port
        && control.Enabled
        && (!context.Request.IsHttps || !HostNodeCertificates.AcceptClient(await context.Connection.GetClientCertificateAsync(), control)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});

var startedAtUtc = DateTimeOffset.UtcNow;
var instanceId = Guid.NewGuid().ToString("N");

app.Lifetime.ApplicationStopped.Register(() =>
{
    ownerStore?.Dispose();
    nodeCertificate?.Dispose();
});

// Liveness Host не зависит от готовности WEB.
app.MapGet("/api/health", () => Results.Ok(new
{
    Service = "TechMES.Host",
    Status = "OK",
    Mode = owner is null ? "Observer" : "Planned",
    SharedAddressEnabled = owner is not null
}));

app.MapGet("/api/host/status", (HostMonitor monitor) => Results.Ok(new
{
    Service = "TechMES.Host",
    DisplayName = "TechMes Host",
    Computer = Environment.MachineName,
    InstanceId = instanceId,
    StartedAtUtc = startedAtUtc,
    ProcessId = Environment.ProcessId,
    Version = typeof(HostMonitor).Assembly.GetName().Version?.ToString(),
    Mode = owner is null ? "Observer" : "Planned",
    SharedAddressEnabled = owner is not null,
    WebReady = monitor.IsWebReady,
    WebHealthUrl = settings.WebHealthUrl,
    Web = monitor.Status,
    Ownership = owner?.Status()
}));

app.MapGet("/api/health/ready", (HostMonitor monitor) => Results.Json(
    new { Service = "TechMES.Host", WebReady = monitor.IsWebReady, Web = monitor.Status },
    statusCode: monitor.IsWebReady ? 200 : 503));

// Маршруты подготовки только читают сеть.
app.MapGet("/api/host/network", () => Results.Ok(HostNetworkInspector.Read()));

app.MapPost("/api/host/network/check", (HostSharedAddressSettings request) =>
    Results.Ok(HostNetworkPolicy.Check(request, HostNetworkInspector.Read())));

app.MapGet("/api/host/shared-address/status", () =>
{
    var network = HostNetworkInspector.Read();

    if (owner is null)
        return Results.Ok(HostNetworkPolicy.Check(shared, network));

    var state = owner.Status();

    return Results.Ok(new HostNetworkCheck(
        shared,
        network,
        state.Errors,
        ["Проверка учитывает VIP, которым уже управляет Host. Владельца смотрите в /api/host/owner/status."]));
});

app.MapGet("/api/host/owner/status", () =>
    owner is null
        ? Results.Json(new { error = "Сначала включите HostControl." }, statusCode: 503)
        : Results.Ok(owner.Status()));

// Ни одна команда изменения VIP не принимается через открытый loopback HTTP.
app.MapPost("/api/host/owner/{action}", async (string action, HttpContext context) =>
{
    if (owner is null)
        return Results.Json(new { error = "HostControl отключён." }, statusCode: 503);

    if (!context.Request.IsHttps || context.Connection.LocalPort != control.Port)
        return Results.StatusCode(403);

    try
    {
        if (action == "apply")
        {
            var certificate = await context.Connection.GetClientCertificateAsync();

            var coordinatorPin = HostOwnerIdentity.Coordinator(shared) == shared.NodeAddress
                ? control.CertificateSha256
                : control.PeerCertificateSha256;

            if (!HostNodeCertificates.Matches(certificate, coordinatorPin, HostNodeCertificates.ClientEku))
                return Results.StatusCode(403);

            var command = await context.Request.ReadFromJsonAsync<HostApplyGrant>()
                ?? throw new InvalidDataException("Пустая команда.");

            await owner.ApplyAsync(command);
        }
        else
        {
            var command = await context.Request.ReadFromJsonAsync<HostOwnerRequest>()
                ?? throw new InvalidDataException("Пустая команда.");

            if (action == "resume")
                await owner.ResumeAsync(command.Operation);
            else if (action is "initialize" or "transfer")
                await owner.ChangeAsync(command, action == "initialize");
            else
                return Results.NotFound();
        }

        return Results.Ok(owner.Status());
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Host owner command {Action} failed", action);

        return Results.Json(new { error = ex.Message }, statusCode: 409);
    }
});

app.Run();