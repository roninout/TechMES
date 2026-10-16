using Microsoft.Extensions.DependencyInjection;
using TechMES.Application.Recipes;
using TechMES.Infrastructure.PostgreSql.Recipes;

namespace TechMES.Infrastructure.PostgreSql;

/// <summary>
/// Подключает чтение рецептов независимо от остальных PostgreSQL-модулей.
/// </summary>
public static class RecipeServiceCollectionExtensions
{
    /// <summary>
    /// Соединение с БД создаётся только при обращении к странице Recipes.
    /// </summary>
    public static IServiceCollection AddRecipeInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRecipeStore, PostgreSqlRecipeStore>();
        return services;
    }
}