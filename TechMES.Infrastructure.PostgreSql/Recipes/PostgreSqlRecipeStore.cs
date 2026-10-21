using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

/// <summary>
/// Работает с существующими таблицами рецептов TecxRecipeAX.
/// Не создаёт и не изменяет схему БД.
/// </summary>
public sealed partial class PostgreSqlRecipeStore : IRecipeStore
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

    /// <summary>Читает рецепт со стадиями и параметрами.</summary>
    public async Task<RecipeSnapshotDto?> GetAsync(int recipeId, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        return await ReadRecipeSnapshotAsync(connection, null, recipeId, ct);
    }

    /// <summary>Использует то же чтение внутри транзакции сохранения редактора.</summary>
    private async Task<RecipeSnapshotDto?> ReadRecipeSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int recipeId, CancellationToken ct)
    {
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

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("recipe_id", recipeId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        RecipeSnapshotDto? result = null;
        var stages = new Dictionary<int, RecipeStageDto>();
        var columns = new Dictionary<int, RecipeColumnDto>();

        while (await reader.ReadAsync(ct))
        {
            result ??= new RecipeSnapshotDto { Recipe = ReadRecipe(reader) };

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
    /// Управляет рецептом в одной транзакции.
    /// Привязка текущего рецепта в RecipePage не изменяется.
    /// </summary>
    public async Task<RecipeChangeResult> ChangeAsync(RecipeChangeRequest request, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? "";

        // Удаление использует отдельную транзакцию с проверкой Current и зависимостей.
        if (request.Operation == RecipeChangeOperation.Delete)
            return await DeleteRecipeAsync(request, ct);

        if (name.Length == 0 || request.SourceRecipeId <= 0 || !Enum.IsDefined(request.Operation))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Select a recipe and enter a valid name." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        // Согласует New/Copy/Rename между экземплярами TechMES.
        // Транзакционная блокировка снимается автоматически при commit или rollback.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380270928, 1);", connection, transaction) { CommandTimeout = GetCommandTimeout() })
            await gate.ExecuteNonQueryAsync(ct);

        const string sourceSql = """
        SELECT r."numb", r."name",
               CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" p WHERE p."baseRecipeId" = r."id")
                    THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END
        FROM public."Recipe" r
        WHERE r."id" = @source_id
        FOR UPDATE OF r;
        """;

        int number;
        int baseRecipeId;
        string sourceName;

        await using (var source = new NpgsqlCommand(sourceSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            source.Parameters.AddWithValue("source_id", request.SourceRecipeId);

            await using var reader = await source.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return new() { Status = RecipeChangeStatus.NotFound, Error = "The source recipe no longer exists. Refresh the list." };

            number = reader.GetInt32(0);
            sourceName = ReadText(reader, 1);
            baseRecipeId = reader.GetInt32(2);
        }

        if (request.Operation == RecipeChangeOperation.Rename && !string.Equals(sourceName, request.ExpectedName, StringComparison.Ordinal))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The recipe was renamed by another client. Refresh the list." };

        const string existsSql = """
        SELECT EXISTS (
            SELECT 1 FROM public."Recipe"
            WHERE lower(btrim("name")) = lower(@name) AND "id" <> @excluded_id
        );
        """;

        await using (var exists = new NpgsqlCommand(existsSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            exists.Parameters.AddWithValue("name", name);
            exists.Parameters.AddWithValue("excluded_id", request.Operation == RecipeChangeOperation.Rename ? request.SourceRecipeId : 0);

            if (await exists.ExecuteScalarAsync(ct) is true)
                return new() { Status = RecipeChangeStatus.Conflict, Error = "A recipe with this name already exists." };
        }

        int recipeId;

        if (request.Operation == RecipeChangeOperation.Rename)
        {
            await using var rename = new NpgsqlCommand("UPDATE public.\"Recipe\" SET \"name\" = @name WHERE \"id\" = @id RETURNING \"id\";", connection, transaction) { CommandTimeout = GetCommandTimeout() };

            rename.Parameters.AddWithValue("name", name);
            rename.Parameters.AddWithValue("id", request.SourceRecipeId);

            recipeId = (int)(await rename.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Recipe rename returned no ID."));
        }
        else
        {
            const string insertSql = """
            INSERT INTO public."Recipe" ("numb", "name", "baseRecipeId")
            VALUES (@number, @name, @base_recipe_id)
            RETURNING "id";
            """;

            await using var insert = new NpgsqlCommand(insertSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };

            insert.Parameters.AddWithValue("number", request.Operation == RecipeChangeOperation.Copy ? number : 0);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("base_recipe_id", baseRecipeId);

            recipeId = (int)(await insert.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Recipe creation returned no ID."));
        }

        if (request.Operation == RecipeChangeOperation.Copy)
        {
            const string copySql = """
            WITH source_stages AS (
                SELECT * FROM public."RecipeStage" WHERE "recipeId" = @source_id
            ), copied_stages AS (
                INSERT INTO public."RecipeStage" ("stageNumb", "name", "auto", "description", "recipeId", "baseRecipeStageId")
                SELECT s."stageNumb", s."name", s."auto", s."description", @recipe_id, s."baseRecipeStageId"
                FROM source_stages s
                WHERE NOT EXISTS (SELECT 1 FROM source_stages GROUP BY "stageNumb" HAVING count(*) > 1)
                ORDER BY s."stageNumb", s."id"
                RETURNING "id", "stageNumb"
            ), copied_parameters AS (
                INSERT INTO public."RecipeParameter" ("name", "value", "description", "active", "recipeStageId", "baseParameterId")
                SELECT p."name", p."value", p."description", p."active", n."id", p."baseParameterId"
                FROM source_stages s
                JOIN copied_stages n ON n."stageNumb" = s."stageNumb"
                JOIN public."RecipeParameter" p ON p."recipeStageId" = s."id"
                RETURNING "id"
            )
            SELECT (SELECT count(*) FROM source_stages), (SELECT count(*) FROM copied_stages), (SELECT count(*) FROM copied_parameters);
            """;

            await using var copy = new NpgsqlCommand(copySql, connection, transaction) { CommandTimeout = GetCommandTimeout() };

            copy.Parameters.AddWithValue("source_id", request.SourceRecipeId);
            copy.Parameters.AddWithValue("recipe_id", recipeId);

            await using var reader = await copy.ExecuteReaderAsync(ct);

            // Номера стадий используются для сопоставления исходных и новых ID.
            // Дубли номеров нельзя копировать через такое сопоставление.
            if (!await reader.ReadAsync(ct) || reader.GetInt64(0) != reader.GetInt64(1))
                return new() { Status = RecipeChangeStatus.Invalid, Error = "The source recipe contains duplicate stage numbers. Correct them before copying." };
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, RecipeId = recipeId };
    }

    /// <summary>
    /// Изменяет стадию в одной транзакции.
    /// Ожидаемый порядок защищает от изменений другого клиента.
    /// </summary>
    public async Task<RecipeStageChangeResult> ChangeStageAsync(int recipeId, RecipeStageChangeRequest request, CancellationToken ct = default)
    {
        if (recipeId <= 0 || request.StageId <= 0 || !Enum.IsDefined(request.Operation) || request.ExpectedStages is null || request.ExpectedStages.Any(stage => stage is null))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid recipe stage request." };

        if (request.Operation == RecipeStageChangeOperation.SetAuto && request.Auto is null)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "An Auto value is required." };

        if (request.Operation == RecipeStageChangeOperation.MoveTo && request.TargetIndex is null)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "A target position is required." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Общая блокировка операций управления рецептами.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380270928, 1);", connection, transaction) { CommandTimeout = GetCommandTimeout() })
            await gate.ExecuteNonQueryAsync(ct);

        await using (var recipe = new NpgsqlCommand("SELECT \"id\" FROM public.\"Recipe\" WHERE \"id\" = @id FOR UPDATE;", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            recipe.Parameters.AddWithValue("id", recipeId);

            if (await recipe.ExecuteScalarAsync(ct) is null)
                return new() { Status = RecipeChangeStatus.NotFound, Error = "The recipe no longer exists. Refresh the list." };
        }

        // При удалении сохраняем порядок блокировок: сначала параметры, затем стадии.
        if (request.Operation == RecipeStageChangeOperation.Delete)
        {
            const string lockSql = """
            SELECT p."id" FROM public."RecipeParameter" p
            JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
            WHERE s."recipeId" = @recipe_id AND s."id" = @stage_id
            ORDER BY p."id" FOR UPDATE OF p;
            """;

            await using var parameters = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
            parameters.Parameters.AddWithValue("recipe_id", recipeId);
            parameters.Parameters.AddWithValue("stage_id", request.StageId);

            await parameters.ExecuteNonQueryAsync(ct);
        }

        var stages = new List<RecipeStagePositionDto>();

        await using (var select = new NpgsqlCommand("SELECT \"id\", \"stageNumb\", \"auto\" FROM public.\"RecipeStage\" WHERE \"recipeId\" = @id ORDER BY \"stageNumb\", \"id\" FOR UPDATE;", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            select.Parameters.AddWithValue("id", recipeId);

            await using var reader = await select.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                stages.Add(new() { Id = reader.GetInt32(0), Number = reader.GetInt32(1), Auto = reader.GetBoolean(2) });
        }

        if (stages.Count != request.ExpectedStages.Count || stages.Where((stage, position) => stage.Id != request.ExpectedStages[position].Id || stage.Number != request.ExpectedStages[position].Number || stage.Auto != request.ExpectedStages[position].Auto).Any())
            return new() { Status = RecipeChangeStatus.Conflict, Error = "Recipe stages were changed by another client. Press Refresh before editing." };

        var index = stages.FindIndex(stage => stage.Id == request.StageId);

        if (index < 0)
            return new() { Status = RecipeChangeStatus.NotFound, Error = "The stage no longer exists in this recipe. Press Refresh." };

        int? selectedStageId = request.StageId;

        if (request.Operation == RecipeStageChangeOperation.SetAuto)
        {
            await using var update = new NpgsqlCommand("UPDATE public.\"RecipeStage\" SET \"auto\" = @auto WHERE \"id\" = @id AND \"recipeId\" = @recipe_id;", connection, transaction) { CommandTimeout = GetCommandTimeout() };
            update.Parameters.AddWithValue("auto", request.Auto!.Value);
            update.Parameters.AddWithValue("id", request.StageId);
            update.Parameters.AddWithValue("recipe_id", recipeId);

            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            if (stages.Any(stage => stage.Number <= 0) || stages.Select(stage => stage.Number).Distinct().Count() != stages.Count)
                return new() { Status = RecipeChangeStatus.Invalid, Error = "Stage numbers must be positive and unique before changing their order." };

            var maxNumber = stages.Max(stage => stage.Number);

            if ((long)maxNumber + stages.Count > int.MaxValue)
                return new() { Status = RecipeChangeStatus.Invalid, Error = "Stage numbers are too large to renumber safely." };

            if (request.Operation == RecipeStageChangeOperation.Delete)
            {
                const string deleteSql = """
                DELETE FROM public."RecipeParameter" WHERE "recipeStageId" = @stage_id;
                DELETE FROM public."RecipeStage" WHERE "id" = @stage_id AND "recipeId" = @recipe_id;
                """;

                await using var delete = new NpgsqlCommand(deleteSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
                delete.Parameters.AddWithValue("stage_id", request.StageId);
                delete.Parameters.AddWithValue("recipe_id", recipeId);

                await delete.ExecuteNonQueryAsync(ct);

                stages.RemoveAt(index);
                selectedStageId = stages.Count == 0 ? null : stages[Math.Min(index, stages.Count - 1)].Id;
            }
            else
            {
                var target = request.Operation == RecipeStageChangeOperation.MoveTo ? request.TargetIndex!.Value : index + (request.Operation == RecipeStageChangeOperation.MoveUp ? -1 : 1);

                if (target < 0 || target >= stages.Count)
                    return new() { Status = RecipeChangeStatus.Invalid, Error = "The target stage position is outside this recipe." };

                // Переносим одну стадию, сохраняя взаимный порядок остальных.
                var moved = stages[index];
                stages.RemoveAt(index);
                stages.Insert(target, moved);
            }

            await RenumberRecipeStagesAsync(connection, transaction, recipeId, stages, maxNumber, ct);
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, SelectedStageId = selectedStageId };
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