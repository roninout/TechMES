using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Application.Param;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.CtApi.Native;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi;

/// <summary>
/// Регистрация Plant SCADA / CtApi infrastructure.
///
/// Runtime.Service вызывает этот extension method и не знает,
/// какая конкретная реализация IPlantScadaGateway будет подключена.
/// </summary>
public static class CtApiServiceCollectionExtensions
{
    public static IServiceCollection AddCtApiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CtApiOptions>(configuration.GetSection("CtApi"));

        var provider = configuration["CtApi:Provider"] ?? "Disabled";

        if (string.Equals(provider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPlantScadaGateway, MockPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider>(_ =>
                new UnavailableEquipmentParamProvider("Param read-only is unavailable in Mock CtApi mode."));
        }
        else if (string.Equals(provider, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPlantScadaGateway, DisabledPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider>(_ =>
                new UnavailableEquipmentParamProvider("Param read-only is unavailable because CtApi is disabled."));
        }
        else if (string.Equals(provider, "CtApi", StringComparison.OrdinalIgnoreCase))
        {
            /*
                Реальный CtApi provider.

                Runtime.Service всё равно работает только через IPlantScadaGateway.
                Детали реального CtApi.dll спрятаны в ICtApiNativeClient.
            */
            services.AddSingleton<ICtApiNativeClient, CtApiNativeClient>();
            services.AddSingleton<IPlantScadaGateway, CtApiPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider, CtApiEquipmentParamProvider>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Неизвестный CtApi:Provider = '{provider}'. " +
                "Поддерживаются значения: Disabled, Mock, CtApi.");
        }

        return services;
    }
}
