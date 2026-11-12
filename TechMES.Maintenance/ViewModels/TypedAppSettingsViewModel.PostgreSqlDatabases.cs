using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Maintenance.ViewModels;

public sealed partial class TypedAppSettingsViewModel
{
    private string _runtimeRecipesDatabase = "RecipeKNG";
    private string _runtimeEventDatabase = "EventPicker";

    // Имя БД рецептов на общей паре серверов PostgreSQL.
    public string RuntimeRecipesDatabase
    {
        get => _runtimeRecipesDatabase;
        set => SetProperty(ref _runtimeRecipesDatabase, value);
    }

    // Имя БД журналов на общей паре серверов PostgreSQL.
    public string RuntimeEventDatabase
    {
        get => _runtimeEventDatabase;
        set => SetProperty(ref _runtimeEventDatabase, value);
    }

    // Диагностика получает вычисленное подключение.
    public string RuntimeRecipeDatabaseConnectionString => ModuleDiagnosticConnectionString(RuntimeRecipesDatabase);

    // Некорректный ввод не прерывает обновление диагностической формы.
    private string ModuleDiagnosticConnectionString(string database)
    {
        if (string.IsNullOrWhiteSpace(RuntimeDatabaseConnectionString) || string.IsNullOrWhiteSpace(database))
            return "";

        try
        {
            return PostgreSqlModuleDatabaseSettings.ConnectionString(RuntimeDatabaseConnectionString, database);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }
}