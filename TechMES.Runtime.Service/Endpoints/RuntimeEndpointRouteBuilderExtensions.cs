namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Единая точка подключения всех HTTP endpoints Runtime.Service.
/// Program.cs вызывает только этот метод, а отдельные модули регистрируются здесь.
/// </summary>
public static class RuntimeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Регистрирует API модулей: health, messages, equipment, info, param,
    /// event log, SOE и низкоуровневую SCADA-диагностику.
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
        app.MapScadaEndpoints();

        return app;
    }
}
