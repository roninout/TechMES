using TechMES.Application.Messages;
using TechMES.Contracts.Runtime;
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
        app.MapGet("/api/health", GetHealthAsync).WithMetadata(new RuntimeControlEndpoint());

        return app;
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