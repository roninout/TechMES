using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Application.Calc;
using TechMES.Application.Param;
using TechMES.Application.Scada;
using TechMES.Application.Soe;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.CtApi.Native;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi;

/// <summary>
/// Регистрация Plant SCADA / CtApi infrastructure.
/// </summary>
public static class CtApiServiceCollectionExtensions
{
    public static IServiceCollection AddCtApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CtApiOptions>(configuration.GetSection("CtApi"));

        var provider = configuration["CtApi:Provider"] ?? "Disabled";

        if (string.Equals(provider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPlantScadaGateway, MockPlantScadaGateway>();

            services.AddSingleton<IEquipmentParamProvider>(_ =>
                new UnavailableEquipmentParamProvider(
                    "Param read-only is unavailable in Mock CtApi mode."));

            services.AddSingleton<IEquipmentSoeProvider>(_ =>
                new UnavailableEquipmentSoeProvider(
                    "SOE is unavailable in Mock CtApi mode."));

            services.AddSingleton<ICalcModelCatalogProvider>(_ =>
                new UnavailableCalcModelCatalogProvider(
                    "Calc SCADA catalog is unavailable in Mock CtApi mode."));
        }
        else if (string.Equals(provider, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPlantScadaGateway, DisabledPlantScadaGateway>();

            services.AddSingleton<IEquipmentParamProvider>(_ =>
                new UnavailableEquipmentParamProvider(
                    "Param read-only is unavailable because CtApi is disabled."));

            services.AddSingleton<IEquipmentSoeProvider>(_ =>
                new UnavailableEquipmentSoeProvider(
                    "SOE is unavailable because CtApi is disabled."));

            services.AddSingleton<ICalcModelCatalogProvider>(_ =>
                new UnavailableCalcModelCatalogProvider(
                    "Calc SCADA catalog is unavailable because CtApi is disabled."));
        }
        else if (string.Equals(provider, "CtApi", StringComparison.OrdinalIgnoreCase))
        {
            /*
             * Один ICtApiNativeClient используется всеми CtApi-модулями.
             * Внутренний gate native client сериализует обращения к CtApi.
             */
            services.AddSingleton<ICtApiNativeClient, CtApiNativeClient>();

            services.AddSingleton<IPlantScadaGateway, CtApiPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider, CtApiEquipmentParamProvider>();
            services.AddSingleton<IEquipmentSoeProvider, CtApiEquipmentSoeProvider>();

            // Calc Catalog не загружается при Runtime startup.
            services.AddSingleton<ICalcModelCatalogProvider, CtApiCalcModelCatalogProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Неизвестный CtApi:Provider = '{provider}'. " + "Поддерживаются значения: Disabled, Mock, CtApi.");
        }

        return services;
    }
}