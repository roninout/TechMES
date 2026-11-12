using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechMES.Application.Recipes;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Infrastructure.PostgreSql.Recipes;

namespace TechMES.Infrastructure.PostgreSql;

public static class RecipeServiceCollectionExtensions
{
    // Регистрирует Recipes и необходимые ему зависимости подключения.
    public static IServiceCollection AddRecipeInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IMainDatabaseConnector, MainDatabaseConnector>();
        services.TryAddSingleton<MainDatabaseWriteGuard>();
        services.TryAddSingleton<PostgreSqlModuleConnections>();

        services.AddScoped<IRecipeStore, PostgreSqlRecipeStore>();

        return services;
    }
}