namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Единая точка подключения всех HTTP endpoints Runtime.Service.
///
/// Program.cs вызывает только этот метод,
/// а каждый функциональный модуль регистрируется отдельно.
/// </summary>
public static class RuntimeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Регистрирует API модулей Runtime.Service.
    ///
    /// Calc endpoints на этом этапе предоставляют только каталог
    /// алгоритмов и ручное read-only тестирование.
    /// </summary>
    public static IEndpointRouteBuilder MapRuntimeEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();
        app.MapMessageEndpoints();
        app.MapEquipmentEndpoints();
        app.MapInfoEndpoints();
        app.MapParamEndpoints();
        app.MapEventLogEndpoints();
        app.MapSoeEndpoints();
        app.MapScadaEndpoints();
        app.MapCalcEndpoints();

        return app;
    }
}