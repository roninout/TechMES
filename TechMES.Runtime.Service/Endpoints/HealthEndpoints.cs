using TechMES.Application.Messages;
using TechMES.Contracts.Runtime;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Runtime health API.
/// WEB использует этот endpoint для footer-диагностики, а будущий configurator сможет
/// по нему быстро понять состояние сервиса и подключенного хранилища.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Подключает endpoint общей диагностики Runtime.Service.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", GetHealthAsync);

        return app;
    }

    /// <summary>
    /// Возвращает статус процесса, имя устройства, версию приложения и состояние message-хранилища.
    /// При ошибке возвращается 503, но с тем же DTO, чтобы WEB мог показать подробность.
    /// </summary>
    private static async Task<IResult> GetHealthAsync(
        IConfiguration configuration,
        IAppRuntimeContext runtime,
        IMessageStore messageStore,
        CancellationToken ct)
    {
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
