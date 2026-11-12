using System.Text.Json.Nodes;
using Npgsql;

namespace TechMES.Infrastructure.PostgreSql.Connections;

// Общие правила имён БД для Runtime и формы Maintenance.
public static class PostgreSqlModuleDatabaseSettings
{
    // Новое имя имеет приоритет; старый профиль используется только для переноса Database.
    public static string ResolveName(string? name, string? legacyConnectionString, string fallback)
    {
        if (name is not null)
            return ValidateName(name);

        if (!string.IsNullOrWhiteSpace(legacyConnectionString))
        {
            var legacy = new NpgsqlConnectionStringBuilder(legacyConnectionString);

            return ValidateName(legacy.Database ?? legacy.Username ?? fallback);
        }

        return ValidateName(fallback);
    }

    // Сохраняет параметры исходного подключения, заменяя только Database.
    public static string ConnectionString(string mainConnectionString, string database)
    {
        return new NpgsqlConnectionStringBuilder(mainConnectionString)
        {
            Database = ValidateName(database)
        }.ConnectionString;
    }

    // Записывает имена БД и удаляет устаревшие отдельные подключения модулей.
    public static void Apply(JsonObject root, string recipesDatabase, string eventDatabase)
    {
        var recipesName = ValidateName(recipesDatabase);
        var eventName = ValidateName(eventDatabase);

        var recipes = Section(root, "Recipes");
        var events = Section(root, "EventDatabase");

        recipes["Database"] = recipesName;
        events["Database"] = eventName;

        recipes.Remove("ConnectionString");
        recipes.Remove("ConnectionStringSecondary");

        events.Remove("ConnectionString");
        events.Remove("ConnectionStringSecondary");

        if (root["ConnectionStrings"] is JsonObject aliases)
            aliases.Remove("EventPicker");
    }

    // Имя передаётся через builder строки подключения и не подставляется в SQL.
    private static string ValidateName(string value)
    {
        var name = value.Trim();

        if (name.Length == 0 || name.Contains('\0'))
            throw new InvalidOperationException("PostgreSQL database name must not be empty or contain NUL.");

        return name;
    }

    // Сохраняет остальные свойства раздела.
    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section)
            return section;

        if (root[name] is not null)
            throw new InvalidOperationException($"{name} must be a JSON object.");

        var created = new JsonObject();
        root[name] = created;

        return created;
    }
}