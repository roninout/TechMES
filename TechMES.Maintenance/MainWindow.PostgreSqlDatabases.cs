using System.Text.Json.Nodes;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    // При первом открытии переносит Database из прежних строк подключения.
    private void LoadPostgreSqlModuleDatabases(JsonObject runtime)
    {
        var recipes = GetString(runtime, "Recipes", "ConnectionString");
        var events = GetString(runtime, "ConnectionStrings", "EventPicker");

        if (string.IsNullOrWhiteSpace(events))
            events = GetString(runtime, "EventDatabase", "ConnectionString");

        TypedAppSettings.RuntimeRecipesDatabase = PostgreSqlModuleDatabaseSettings.ResolveName(runtime["Recipes"]?["Database"]?.GetValue<string>(), recipes, "RecipeKNG");
        TypedAppSettings.RuntimeEventDatabase = PostgreSqlModuleDatabaseSettings.ResolveName(runtime["EventDatabase"]?["Database"]?.GetValue<string>(), events, "EventPicker");
    }

    // Сохраняет имена БД и удаляет прежние отдельные подключения модулей.
    private void ApplyPostgreSqlModuleDatabases(JsonObject root)
    {
        PostgreSqlModuleDatabaseSettings.Apply(root, TypedAppSettings.RuntimeRecipesDatabase, TypedAppSettings.RuntimeEventDatabase);
    }
}