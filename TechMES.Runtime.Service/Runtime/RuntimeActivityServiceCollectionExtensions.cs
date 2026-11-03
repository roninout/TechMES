using TechMES.Application.Scada;

namespace TechMES.Runtime.Service.Runtime;

public static class RuntimeActivityServiceCollectionExtensions
{
    /// <summary>
    /// Внутренний adapter сохраняется под DI-ключом.
    /// Обычный IPlantScadaGateway всегда получает защиту активности.
    /// </summary>
    public static IServiceCollection AddRuntimeActivityProtection(this IServiceCollection services)
    {
        var original = services.LastOrDefault(item => item.ServiceType == typeof(IPlantScadaGateway) && !item.IsKeyedService);

        if (original?.ImplementationType is null || original.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Register a singleton IPlantScadaGateway implementation before Runtime activity protection.");

        services.Remove(original);

        services.AddKeyedSingleton(typeof(IPlantScadaGateway), "Runtime.Inner.Scada", original.ImplementationType);
        services.AddSingleton<IPlantScadaGateway>(provider => new RuntimeGuardedScadaGateway(provider.GetRequiredKeyedService<IPlantScadaGateway>("Runtime.Inner.Scada"), provider.GetRequiredService<RuntimeActivityState>()));

        return services;
    }
}