using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechMES.Application.Info;
using TechMES.Application.Messages;
using TechMES.Infrastructure.PostgreSql.Info;
using TechMES.Infrastructure.PostgreSql.Messages;

namespace TechMES.Infrastructure.PostgreSql;

/// <summary>
/// Регистрация PostgreSQL-инфраструктуры в DI-контейнере.
///
/// DI (Dependency Injection) позволяет Runtime.Service сказать:
/// "Мне нужен IMessageStore", а конкретную реализацию подключить здесь.
/// Сегодня это PostgreSQL, завтра это может быть SQL Server, SQLite или другой адаптер.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Регистрируем PostgreSQL-адаптер как реализацию IMessageStore.
        // Runtime.Service продолжает работать только с интерфейсом IMessageStore
        // и не зависит от Npgsql или SQL-запросов напрямую.
        services.AddScoped<IMessageStore, PostgreSqlMessageStore>();
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();

        return services;
    }

    public static IServiceCollection AddPostgreSqlInfoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();

        return services;
    }
}
