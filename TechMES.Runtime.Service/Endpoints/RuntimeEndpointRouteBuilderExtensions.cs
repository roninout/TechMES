namespace TechMES.Runtime.Service.Endpoints;

public static class RuntimeEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();
        app.MapMessageEndpoints();
        app.MapEquipmentEndpoints();
        app.MapInfoEndpoints();
        app.MapScadaEndpoints();

        return app;
    }
}
