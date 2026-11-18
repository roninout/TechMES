using System.ComponentModel;
using System.ServiceProcess;
using TechMES.Application.Messages;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Runtime.Service.Calc;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// Проверка процесса доступна независимо от готовности рабочих зависимостей.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/status", (RuntimeActivityState activity) => Results.Ok(activity.GetStatus())).WithMetadata(new RuntimeControlEndpoint());
        app.MapGet("/api/runtime/diagnostics", GetDiagnostics).WithMetadata(new RuntimeControlEndpoint());
        app.MapGet("/api/health", GetHealthAsync).WithMetadata(new RuntimeControlEndpoint());

        return app;
    }

    /// <summary>Читает локальные статусы Windows и готовый снимок PostgreSQL без рабочих команд.</summary>
    private static IResult GetDiagnostics(RuntimeActivityState activity, CalcServiceHeartbeatRegistry registry, MainDatabaseWriteGuard writeGuard, PostgreSqlClusterMonitor cluster)
    {
        var service = ReadCalcServiceStatus();
        var lease = registry.GetStatus();
        var runtime = activity.GetStatus();
        var ownsLocalLease = lease.Availability == CalcServiceAvailabilityDto.Online
            && string.Equals(lease.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        // SCM видит остановку сразу, даже пока предыдущий heartbeat формально действует.
        var status = service switch
        {
            "Missing" => "N/A",
            "Stopped" => "Offline",
            "Paused" => "Paused",
            "StartPending" or "StopPending" or "ContinuePending" or "PausePending" => "Changing",
            _ when service == "Running" || ownsLocalLease => writeGuard.GetStatus().WritesBlocked ? "Blocked"
                : runtime.Mode == "Standby" ? "Standby"
                : ownsLocalLease && runtime.CanExecuteCommands ? "Online"
                : "Waiting",
            _ => "Unknown"
        };

        return Results.Ok(new RuntimeDiagnosticsResponse(new(status, service, lease.MachineName, lease.LastHeartbeatReceivedAtUtc), cluster.GetHealth()));
    }

    /// <summary>Читает состояние локальной службы без запуска PowerShell или обращения к удалённому SCM.</summary>
    private static string? ReadCalcServiceStatus()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var service = new ServiceController("TechMES.Calc.Service");
            return service.Status.ToString();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return "Missing";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static async Task<IResult> GetHealthAsync(IConfiguration configuration, IAppRuntimeContext runtime, RuntimeActivityState activity, IServiceProvider services, CancellationToken ct)
    {
        var state = activity.GetStatus();

        var response = new RuntimeHealthResponse
        {
            Status = state.CanExecuteCommands ? "OK" : state.Mode.ToUpperInvariant(),
            Service = "TechMES.Runtime.Service",
            DeviceName = runtime.DeviceName,
            UserName = runtime.UserName,
            MachineName = runtime.MachineName,
            AppVersion = runtime.AppVersion,
            MessageStorageProvider = configuration["MessageStorage:Provider"] ?? "InMemory",
            Database = "NOT_CHECKED",
            Activity = state,
            Time = DateTime.Now
        };

        // IMessageStore разрешаем из DI только для активного Runtime.
        if (!state.CanExecuteCommands)
            return Results.Ok(response);

        try
        {
            using var lease = activity.Acquire();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

            response.ActiveMessageCount = await services.GetRequiredService<IMessageStore>().GetActiveMessageCountAsync(stop.Token);
            response.Database = "OK";

            return Results.Ok(response);
        }
        catch (Exception ex) when (ex is RuntimeNotActiveException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            response.Activity = activity.GetStatus();
            response.Status = response.Activity.Mode.ToUpperInvariant();

            return Results.Ok(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            response.Status = "ERROR";
            response.Database = "ERROR";
            response.Error = ex.Message;

            return Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}