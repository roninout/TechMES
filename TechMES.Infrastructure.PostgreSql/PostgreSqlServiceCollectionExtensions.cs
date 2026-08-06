using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechMES.Application.Calc;
using TechMES.Application.EventLog;
using TechMES.Application.Info;
using TechMES.Application.Messages;
using TechMES.Application.Param;
using TechMES.Infrastructure.PostgreSql.Calc;
using TechMES.Infrastructure.PostgreSql.EventLog;
using TechMES.Infrastructure.PostgreSql.Info;
using TechMES.Infrastructure.PostgreSql.Messages;
using TechMES.Infrastructure.PostgreSql.Param;

namespace TechMES.Infrastructure.PostgreSql;

/// <summary>
/// Регистрирует PostgreSQL-адаптеры TechMES в DI-контейнере.
/// Runtime.Service зависит от интерфейсов Application, а не от Npgsql.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует основную PostgreSQL-инфраструктуру Messages, Info,
    /// Param и Calculations.
    /// </summary>
    public static IServiceCollection AddPostgreSqlInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<PostgreSqlDatabaseBootstrapper>();
        services.AddScoped<IMessageStore, PostgreSqlMessageStore>();
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();
        services.TryAddScoped<IParamTuneStore, PostgreSqlParamTuneStore>();
        services.TryAddScoped<ICalcJobStore, PostgreSqlCalcJobStore>();
        services.TryAddScoped<ICalcJobStateStore, PostgreSqlCalcJobStateStore>();

        return services;
    }

    /// <summary>
    /// Регистрирует модули основной srd_db независимо от выбранного
    /// провайдера Messages.
    ///
    /// CalcJobStore находится здесь обязательно, потому что Runtime может
    /// использовать InMemory Messages, но задания расчётов всё равно
    /// должны храниться в основной PostgreSQL-БД.
    /// </summary>
    public static IServiceCollection AddPostgreSqlInfoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<PostgreSqlDatabaseBootstrapper>();
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();
        services.TryAddScoped<IParamTuneStore, PostgreSqlParamTuneStore>();
        services.TryAddScoped<ICalcJobStore, PostgreSqlCalcJobStore>();
        services.TryAddScoped<ICalcJobStateStore, PostgreSqlCalcJobStateStore>();

        return services;
    }

    /// <summary>
    /// Регистрирует отдельное EventPicker-хранилище для Operation actions
    /// и Alarm history.
    /// </summary>
    public static IServiceCollection AddPostgreSqlEventLogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IEventLogStore, PostgreSqlEventLogStore>();
        return services;
    }
}