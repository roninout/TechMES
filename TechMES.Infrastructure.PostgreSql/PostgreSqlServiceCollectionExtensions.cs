using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Application.Messages;
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

        return services;
    }
}
