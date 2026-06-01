using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechMES.Application.EventLog;
using TechMES.Application.Info;
using TechMES.Application.Messages;
using TechMES.Infrastructure.PostgreSql.EventLog;
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
    /// <summary>
    /// Регистрирует PostgreSQL-реализации для модулей, которые используют основную БД srd_db:
    /// Messages и Info. Вызывается из Program.cs, когда MessageStorage:Provider = PostgreSql.
    /// </summary>
    public static IServiceCollection AddPostgreSqlInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Регистрируем PostgreSQL-адаптер как реализацию IMessageStore.
        // Runtime.Service продолжает работать только с интерфейсом IMessageStore
        // и не зависит от Npgsql или SQL-запросов напрямую.
        services.AddScoped<IMessageStore, PostgreSqlMessageStore>();
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();

        return services;
    }

    /// <summary>
    /// Регистрирует только Info-хранилище.
    /// Используется отдельно, потому что Info работает с существующими таблицами WPF-БД
    /// независимо от того, какой provider выбран для Messages.
    /// </summary>
    public static IServiceCollection AddPostgreSqlInfoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IEquipmentInfoStore, PostgreSqlEquipmentInfoStore>();

        return services;
    }

    /// <summary>
    /// Регистрирует EventLog-хранилище для Operation actions и Alarm history.
    /// Оно использует отдельную EventPicker connection string.
    /// </summary>
    public static IServiceCollection AddPostgreSqlEventLogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IEventLogStore, PostgreSqlEventLogStore>();

        return services;
    }
}
