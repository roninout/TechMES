using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

/// <summary>
/// Читает существующие таблицы TecxRecipeAX.
/// Не создаёт и не изменяет схему БД.
/// </summary>
public sealed class PostgreSqlRecipeStore : IRecipeStore
{
    private readonly IConfiguration _configuration;

    public PostgreSqlRecipeStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Получает привязки объектов и список рецептов без загрузки всех стадий.
    /// </summary>
    public async Task<RecipeCatalogDto> GetCatalogAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
        SELECT "id", "page", "recipeId", "baseRecipeId"
        FROM public."RecipePage"
        ORDER BY "page", "id";

        SELECT "id", "numb", "name", "baseRecipeId"
        FROM public."Recipe"
        ORDER BY "name", "id";
        """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = GetCommandTimeout() };
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new RecipeCatalogDto();

        while (await reader.ReadAsync(ct))
        {
            result.Pages.Add(new RecipePageDto
            {
                Id = reader.GetInt32(0),
                Page = ReadText(reader, 1),
                RecipeId = reader.GetInt32(2),
                BaseRecipeId = reader.GetInt32(3)
            });
        }

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct))
            result.Recipes.Add(ReadRecipe(reader));

        return result;
    }

    /// <summary>
    /// Один SELECT возвращает согласованный снимок выбранного рецепта.
    /// LEFT JOIN сохраняет рецепты без стадий и стадии без параметров.
    /// Параметры сопоставляются по ID, поэтому одинаковые имена не смешивают значения.
    /// </summary>
    public async Task<RecipeSnapshotDto?> GetAsync(int recipeId, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
        SELECT r."id", r."numb", r."name", r."baseRecipeId",
               s."id", s."stageNumb", s."name", s."auto", s."baseRecipeStageId", s."description",
               p."id", p."baseParameterId", p."value", p."active",
               COALESCE(b."name", p."name", ''), COALESCE(b."description", p."description", ''),
               COALESCE(b."type", 2147483647), COALESCE(b."tag", ''),
               COALESCE(b."kind", 0), COALESCE(b."format", 0)
        FROM public."Recipe" r
        LEFT JOIN public."RecipeStage" s ON s."recipeId" = r."id"
        LEFT JOIN public."RecipeParameter" p ON p."recipeStageId" = s."id"
        LEFT JOIN public."BaseParameter" b ON b."id" = p."baseParameterId"
        WHERE r."id" = @recipe_id
        ORDER BY s."stageNumb", s."id", b."type", b."name", p."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("recipe_id", recipeId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        RecipeSnapshotDto? result = null;
        var stages = new Dictionary<int, RecipeStageDto>();
        var columns = new Dictionary<int, RecipeColumnDto>();

        while (await reader.ReadAsync(ct))
        {
            result ??= new RecipeSnapshotDto { Recipe = ReadRecipe(reader) };

            // Рецепт существует, но стадий в нём пока нет.
            if (reader.IsDBNull(4))
                continue;

            var stageId = reader.GetInt32(4);

            if (!stages.TryGetValue(stageId, out var stage))
            {
                stage = new RecipeStageDto
                {
                    Id = stageId,
                    Number = reader.GetInt32(5),
                    Name = ReadText(reader, 6),
                    Auto = reader.GetBoolean(7),
                    BaseStageId = reader.GetInt32(8),
                    TagPrefix = ReadText(reader, 9)
                };

                stages.Add(stageId, stage);
                result.Stages.Add(stage);
            }

            // Стадия существует, но параметров в ней пока нет.
            if (reader.IsDBNull(10))
                continue;

            var baseParameterId = reader.GetInt32(11);

            columns.TryAdd(baseParameterId, new RecipeColumnDto
            {
                Id = baseParameterId,
                Name = ReadText(reader, 14),
                Description = ReadText(reader, 15),
                Order = reader.GetInt32(16),
                TagSuffix = ReadText(reader, 17),
                Kind = (RecipeParameterKind)reader.GetInt32(18),
                Format = (RecipeParameterFormat)reader.GetInt32(19)
            });

            var parameter = new RecipeParameterDto
            {
                Id = reader.GetInt32(10),
                Value = reader.GetDouble(12),
                Active = reader.GetBoolean(13)
            };

            // Повтор одного базового параметра в стадии нельзя молча перезаписывать.
            if (!stage.Parameters.TryAdd(baseParameterId, parameter))
                throw new InvalidOperationException($"Recipe {recipeId}, stage {stageId}: duplicate base parameter {baseParameterId}.");
        }

        if (result is not null)
            result.Columns = columns.Values.OrderBy(column => column.Order).ThenBy(column => column.Name).ThenBy(column => column.Id).ToList();

        return result;
    }

    /// <summary>
    /// Сохраняет одну ячейку в короткой транзакции.
    /// Блокировки берутся только на время сохранения, а не на время открытого диалога.
    /// </summary>
    public async Task<RecipeParameterSaveResult> SaveParameterAsync(int recipeId, RecipeParameterSaveRequest request, CancellationToken ct = default)
    {
        if (recipeId <= 0 || request.ParameterId <= 0 || request.StageId <= 0 || request.BaseParameterId <= 0)
            return new() { Status = RecipeParameterSaveStatus.InvalidValue, Error = "Invalid recipe parameter identity." };

        if (request.Value is not double value || !double.IsFinite(value) || request.ExpectedValue is not double expected || !double.IsFinite(expected))
            return new() { Status = RecipeParameterSaveStatus.InvalidValue, Error = "A finite numeric value is required." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        const string selectSql = """
        SELECT p."value", b."kind"
        FROM public."RecipeParameter" p
        JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
        JOIN public."BaseParameter" b ON b."id" = p."baseParameterId"
        WHERE p."id" = @parameter_id AND s."id" = @stage_id
          AND s."recipeId" = @recipe_id AND b."id" = @base_parameter_id
        FOR UPDATE OF p, s, b;
        """;

        double current;
        int kind;

        await using (var select = new NpgsqlCommand(selectSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            select.Parameters.AddWithValue("parameter_id", request.ParameterId);
            select.Parameters.AddWithValue("stage_id", request.StageId);
            select.Parameters.AddWithValue("recipe_id", recipeId);
            select.Parameters.AddWithValue("base_parameter_id", request.BaseParameterId);

            await using var reader = await select.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return new() { Status = RecipeParameterSaveStatus.NotFound, Error = "The parameter or recipe structure no longer exists. Refresh the recipe." };

            current = reader.GetDouble(0);
            kind = reader.GetInt32(1);
        }

        if (kind != (int)RecipeParameterKind.InputField && kind != (int)RecipeParameterKind.CheckBox)
            return new() { Status = RecipeParameterSaveStatus.InvalidValue, Error = "This parameter type cannot be edited." };

        if (kind == (int)RecipeParameterKind.CheckBox && value != 0 && value != 1)
            return new() { Status = RecipeParameterSaveStatus.InvalidValue, Error = "A checkbox value must be 0 or 1." };

        // Значения double передаются JSON без округления.
        // Допуск здесь использовать нельзя: небольшое чужое изменение тоже должно вызвать конфликт.
        if (current != expected)
            return new() { Status = RecipeParameterSaveStatus.Conflict, Error = "The value was changed by another client. Refresh the recipe and try again." };

        const string updateSql = """
        UPDATE public."RecipeParameter"
        SET "value" = @value
        WHERE "id" = @parameter_id AND "recipeStageId" = @stage_id
          AND "baseParameterId" = @base_parameter_id AND "value" = @expected
        RETURNING "value";
        """;

        await using var update = new NpgsqlCommand(updateSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };

        update.Parameters.AddWithValue("value", value);
        update.Parameters.AddWithValue("parameter_id", request.ParameterId);
        update.Parameters.AddWithValue("stage_id", request.StageId);
        update.Parameters.AddWithValue("base_parameter_id", request.BaseParameterId);
        update.Parameters.AddWithValue("expected", expected);

        var saved = await update.ExecuteScalarAsync(ct);

        if (saved is not double savedValue)
            return new() { Status = RecipeParameterSaveStatus.Conflict, Error = "The parameter changed during saving. Refresh the recipe." };

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeParameterSaveStatus.Saved, Value = savedValue };
    }

    /// <summary>
    /// Создаёт соединение с отдельной БД рецептов.
    /// Строка подключения основной БД TechMES не подменяется.
    /// </summary>
    private NpgsqlConnection CreateConnection()
    {
        var connectionString = _configuration["Recipes:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Recipes:ConnectionString is not configured.");

        return new NpgsqlConnection(connectionString);
    }

    /// <summary>
    /// Таймаут SQL-команды в секундах. Это не интервал автоматического опроса.
    /// </summary>
    private int GetCommandTimeout() => Math.Clamp(_configuration.GetValue("Recipes:CommandTimeoutSeconds", 30), 1, 300);

    /// <summary>
    /// Оба запроса возвращают заголовок рецепта первыми четырьмя колонками.
    /// </summary>
    private static RecipeListItemDto ReadRecipe(NpgsqlDataReader reader)
    {
        return new RecipeListItemDto
        {
            Id = reader.GetInt32(0),
            Number = reader.GetInt32(1),
            Name = ReadText(reader, 2),
            BaseRecipeId = reader.IsDBNull(3) ? null : reader.GetInt32(3)
        };
    }

    /// <summary>
    /// Старые записи могут содержать NULL в текстовых полях.
    /// </summary>
    private static string ReadText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
}