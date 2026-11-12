using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechMES.Application.Calc;
using TechMES.Application.EventLog;
using TechMES.Application.Info;
using TechMES.Application.Messages;
using TechMES.Application.Param;
using TechMES.Infrastructure.PostgreSql.Calc;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Infrastructure.PostgreSql.EventLog;
using TechMES.Infrastructure.PostgreSql.Info;
using TechMES.Infrastructure.PostgreSql.Messages;
using TechMES.Infrastructure.PostgreSql.Param;

namespace TechMES.Infrastructure.PostgreSql;

public static class PostgreSqlServiceCollectionExtensions
{
    // Регистрирует основную PostgreSQL-инфраструктуру.
    public static IServiceCollection AddPostgreSqlInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IMainDatabaseConnector, MainDatabaseConnector>();
        services.TryAddSingleton<MainDatabaseWriteGuard>();
        services.TryAddSingleton<PostgreSqlModuleConnections>();
        services.TryAddSingleton<MainDatabaseFailover>();
        services.TryAddSingleton<PostgreSqlDatabaseBootstrapper>();

        services.AddScoped<IMessageStore, PostgreSqlMessageStore>();
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();
        services.TryAddScoped<IParamTuneStore, PostgreSqlParamTuneStore>();
        services.TryAddScoped<ICalcJobStore, PostgreSqlCalcJobStore>();
        services.TryAddScoped<ICalcJobStateStore, PostgreSqlCalcJobStateStore>();

        return services;
    }

    // Регистрирует модули основной БД независимо от провайдера Messages.
    public static IServiceCollection AddPostgreSqlInfoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IMainDatabaseConnector, MainDatabaseConnector>();
        services.TryAddSingleton<MainDatabaseWriteGuard>();
        services.TryAddSingleton<PostgreSqlModuleConnections>();
        services.TryAddSingleton<MainDatabaseFailover>();
        services.TryAddSingleton<PostgreSqlDatabaseBootstrapper>();

        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();
        services.TryAddScoped<IParamTuneStore, PostgreSqlParamTuneStore>();
        services.TryAddScoped<ICalcJobStore, PostgreSqlCalcJobStore>();
        services.TryAddScoped<ICalcJobStateStore, PostgreSqlCalcJobStateStore>();
        services.TryAddScoped<TechMES.Application.Users.IUserStateStore, TechMES.Infrastructure.PostgreSql.Users.PostgreSqlUserStateStore>();

        return services;
    }

    // Регистрирует EventPicker и необходимые ему зависимости подключения.
    public static IServiceCollection AddPostgreSqlEventLogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IMainDatabaseConnector, MainDatabaseConnector>();
        services.TryAddSingleton<MainDatabaseWriteGuard>();
        services.TryAddSingleton<PostgreSqlModuleConnections>();
        services.TryAddScoped<IEventLogStore, PostgreSqlEventLogStore>();

        return services;
    }
}