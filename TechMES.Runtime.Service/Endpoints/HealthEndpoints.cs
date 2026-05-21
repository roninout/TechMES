using TechMES.Application.Messages;
using TechMES.Contracts.Runtime;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", GetHealthAsync);

        return app;
    }

    private static async Task<IResult> GetHealthAsync(
        IConfiguration configuration,
        IAppRuntimeContext runtime,
        IMessageStore messageStore,
        CancellationToken ct)
    {
        // Health endpoint нужен не только для проверки "процесс жив",
        // но и для WEB-интерфейса и будущего WPF Configurator.
        try
        {
            var activeCount = await messageStore.GetActiveMessageCountAsync(ct);

            return Results.Ok(new RuntimeHealthResponse
            {
                Status = "OK",
                Service = "TechMES.Runtime.Service",
                DeviceName = runtime.DeviceName,
                UserName = runtime.UserName,
                MachineName = runtime.MachineName,
                AppVersion = runtime.AppVersion,
                MessageStorageProvider = configuration["MessageStorage:Provider"] ?? "InMemory",
                Database = "OK",
                ActiveMessageCount = activeCount,
                Time = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            return Results.Json(
                new RuntimeHealthResponse
                {
                    Status = "ERROR",
                    Service = "TechMES.Runtime.Service",
                    DeviceName = runtime.DeviceName,
                    UserName = runtime.UserName,
                    MachineName = runtime.MachineName,
                    AppVersion = runtime.AppVersion,
                    MessageStorageProvider = configuration["MessageStorage:Provider"] ?? "InMemory",
                    Database = "ERROR",
                    ActiveMessageCount = 0,
                    Time = DateTime.Now,
                    Error = ex.Message
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
