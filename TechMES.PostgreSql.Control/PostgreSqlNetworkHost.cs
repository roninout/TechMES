using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

// Размещает HTTPS listener внутри существующего процесса Runtime или Agent.
public sealed class PostgreSqlNetworkHost(IOptions<PostgreSqlNetworkOptions> network, IOptions<PostgreSqlControlOptions> control, PostgreSqlSwitchInbox inbox, PostgreSqlNodeMonitor monitor, ILogger<PostgreSqlNetworkHost> logger) : BackgroundService
{
    // Открывает listener только при включённой сети и локальном владении контроллером.
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!network.Value.Enabled)
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (!monitor.GetHealth().OwnsController)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);

                var settings = network.Value;

                settings.Validate(control.Value);

                using var certificate = PostgreSqlNodeCertificates.LoadLocal(settings);

                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });

                builder.Configuration.Sources.Clear();
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();

                builder.WebHost.ConfigureKestrel(server =>
                {
                    server.Limits.MaxRequestBodySize = 65536;

                    server.Listen(IPAddress.Parse(settings.NodeAddress), settings.Port, endpoint =>
                    {
                        endpoint.Protocols = HttpProtocols.Http1;

                        endpoint.UseHttps(https =>
                        {
                            https.ServerCertificate = certificate;
                            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                            https.ClientCertificateValidation = (client, _, _) => PostgreSqlNodeCertificates.AcceptClient(client, settings);
                        });
                    });
                });

                await using var app = builder.Build();

                app.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("PostgreSQL network request rejected: {ErrorType}.", ex.GetType().Name);

                        context.Response.StatusCode = 409;

                        await context.Response.WriteAsJsonAsync(new { error = ex.Message }, context.RequestAborted);
                    }
                });

                app.MapGet("/v1/status", () => inbox.Status());
                app.MapGet("/v1/switches", () => inbox.History());

                app.MapPost("/v1/reserve", async (PostgreSqlSwitchPlan plan, CancellationToken token) =>
                {
                    await inbox.ReserveAsync(plan, token);
                    return Results.Ok();
                });

                app.MapPost("/v1/phases/{mode}", async (string mode, PostgreSqlPhaseRequest phase, CancellationToken token) => Results.Ok(await inbox.SubmitAsync(mode, phase, token)));

                app.MapPost("/v1/read/{mode}", (string mode, PostgreSqlSwitchPlan plan) => Results.Ok(inbox.Read(plan, mode)));

                app.MapPost("/v1/complete", async (PostgreSqlSwitchPlan plan, CancellationToken token) =>
                {
                    await inbox.CompleteAsync(plan, token);
                    return Results.Ok();
                });

                app.MapPost("/v1/cancel", async (PostgreSqlSwitchPlan plan, CancellationToken token) =>
                {
                    await inbox.CancelAsync(plan, token);
                    return Results.Ok();
                });

                await app.StartAsync(ct);

                logger.LogInformation("PostgreSQL mTLS listener started: {Node}:{Port}.", settings.NodeAddress, settings.Port);

                while (monitor.GetHealth().OwnsController)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);

                await app.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // Ошибка listener не должна останавливать приложение Runtime.
                logger.LogError(ex, "PostgreSQL network listener is unavailable. Check network settings and certificates.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
            }
        }
    }
}