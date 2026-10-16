namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Единая точка подключения всех HTTP endpoints Runtime.Service.
/// </summary>
public static class RuntimeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Регистрирует HTTP API модулей Runtime.Service.
    /// </summary>
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();
        app.MapMessageEndpoints();
        app.MapEquipmentEndpoints();
        app.MapInfoEndpoints();
        app.MapParamEndpoints();
        app.MapEventLogEndpoints();
        app.MapSoeEndpoints();
        app.MapActiveAlarmEndpoints();
        app.MapAlarmSummaryEndpoints();
        app.MapEventJournalEndpoints();
        app.MapScadaEndpoints();
        app.MapCalcEndpoints();
        app.MapRecipeEndpoints();

        return app;
    }
}