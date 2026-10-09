using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Application.Calc;
using TechMES.Application.Param;
using TechMES.Application.Scada;
using TechMES.Application.Soe;
using TechMES.Application.Alarms;
using TechMES.Application.Events;
using TechMES.Infrastructure.CtApi.Gateways;
using TechMES.Infrastructure.CtApi.Native;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi;

/// <summary>
/// Регистрация Plant SCADA / CtApi infrastructure.
/// </summary>
public static class CtApiServiceCollectionExtensions
{
    public static IServiceCollection AddCtApiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CtApiOptions>(configuration.GetSection("CtApi"));

        var provider = configuration["CtApi:Provider"] ?? "Disabled";

        if (string.Equals(provider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IActiveAlarmProvider, UnavailableActiveAlarmProvider>();
            services.AddSingleton<IAlarmSummaryProvider, UnavailableAlarmSummaryProvider>();
            services.AddSingleton<IEventJournalProvider, UnavailableEventJournalProvider>();
            services.AddSingleton<IPlantScadaGateway, MockPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider>(_ => new UnavailableEquipmentParamProvider("Param read-only is unavailable in Mock CtApi mode."));
            services.AddSingleton<IEquipmentSoeProvider>(_ => new UnavailableEquipmentSoeProvider("SOE is unavailable in Mock CtApi mode."));
            services.AddSingleton<ICalcModelCatalogProvider>(_ => new UnavailableCalcModelCatalogProvider("Calc SCADA catalog is unavailable in Mock CtApi mode."));
        }
        else if (string.Equals(provider, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IActiveAlarmProvider, UnavailableActiveAlarmProvider>();
            services.AddSingleton<IAlarmSummaryProvider, UnavailableAlarmSummaryProvider>();
            services.AddSingleton<IEventJournalProvider, UnavailableEventJournalProvider>();
            services.AddSingleton<IPlantScadaGateway, DisabledPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider>(_ => new UnavailableEquipmentParamProvider("Param read-only is unavailable because CtApi is disabled."));
            services.AddSingleton<IEquipmentSoeProvider>(_ => new UnavailableEquipmentSoeProvider("SOE is unavailable because CtApi is disabled."));
            services.AddSingleton<ICalcModelCatalogProvider>(_ => new UnavailableCalcModelCatalogProvider("Calc SCADA catalog is unavailable because CtApi is disabled."));
        }
        else if (string.Equals(provider, "CtApi", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IActiveAlarmProvider, CtApiActiveAlarmProvider>();
            services.AddSingleton<IAlarmSummaryProvider, CtApiAlarmSummaryProvider>();
            services.AddSingleton<IEventJournalProvider, UnavailableEventJournalProvider>();

            // Все модули используют один failover client; native gate сериализует вызовы DLL.
            services.AddSingleton<ICtApiNativeClient>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CtApiOptions>>().Value;
                var logger = serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CtApiNativeClient>>();

                CtApiNativeClient Create(string server) => new(
                    Microsoft.Extensions.Options.Options.Create(new CtApiOptions
                    {
                        Path = options.Path,
                        Server = server,
                        User = options.User,
                        Password = options.Password,
                        HealthCheckTag = options.HealthCheckTag
                    }),
                    logger);

                return new CtApiFailoverClient(options, Create(options.Server), Create(options.ServerSecondary), serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CtApiFailoverClient>>());
            });

            services.AddSingleton<IPlantScadaGateway, CtApiPlantScadaGateway>();
            services.AddSingleton<IEquipmentParamProvider, CtApiEquipmentParamProvider>();
            services.AddSingleton<IEquipmentSoeProvider, CtApiEquipmentSoeProvider>();

            // Calc Catalog не загружается при старте Runtime.
            services.AddSingleton<ICalcModelCatalogProvider, CtApiCalcModelCatalogProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Неизвестный CtApi:Provider = '{provider}'. Поддерживаются значения: Disabled, Mock, CtApi.");
        }

        return services;
    }
}