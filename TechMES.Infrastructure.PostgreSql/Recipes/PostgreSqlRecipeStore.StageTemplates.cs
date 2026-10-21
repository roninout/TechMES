using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

/// <summary>
/// Чтение шаблонов и добавление стадии с параметрами из существующей схемы TecxRecipeAX.
/// </summary>
public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>
    /// Читает шаблоны семейства выбранного рецепта.
    /// LEFT JOIN позволяет показать и шаблон без параметров.
    /// </summary>
    public async Task<RecipeStageTemplateCatalogDto?> GetStageTemplatesAsync(int recipeId, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var link = await FindTemplateParameterLinkAsync(connection, null, ct);

        var sql = $"""
        WITH selected_recipe AS (
            SELECT CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" p WHERE p."baseRecipeId" = r."id")
                        THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END AS family_id
            FROM public."Recipe" r WHERE r."id" = @recipe_id
        )
        SELECT r.family_id, b."id", b."name", b."description", count(l.{link.ParameterColumn})::integer
        FROM selected_recipe r
        LEFT JOIN public."BaseRecipeStage" b ON b."baseRecipeId" = r.family_id
        LEFT JOIN {link.Table} l ON l.{link.StageColumn} = b."id"
        GROUP BY r.family_id, b."id", b."name", b."description"
        ORDER BY b."name", b."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("recipe_id", recipeId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        RecipeStageTemplateCatalogDto? result = null;

        while (await reader.ReadAsync(ct))
        {
            result ??= new() { BaseRecipeId = reader.GetInt32(0) };

            if (!reader.IsDBNull(1))
                result.Templates.Add(new() { Id = reader.GetInt32(1), Name = ReadText(reader, 2), Code = ReadText(reader, 3), ParameterCount = reader.GetInt32(4) });
        }

        return result;
    }

    /// <summary>
    /// Добавляет стадию из шаблона семейства.
    /// Вставка в середину сохраняет ID и значения существующих стадий.
    /// </summary>
    public async Task<RecipeStageChangeResult> AddStageAsync(int recipeId, RecipeStageAddRequest request, CancellationToken ct = default)
    {
        var prefix = request.TagPrefix?.Trim() ?? "";

        if (recipeId <= 0 || request.BaseStageId <= 0 || request.RecipePageId is <= 0 || request.InsertBeforeStageId is <= 0 || request.ExpectedStages is null || request.ExpectedStages.Any(stage => stage is null))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid add stage request." };

        if (!request.AutoAssignTagPrefix && !IsValidStagePrefix(prefix))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Enter a PLC tag prefix without spaces or control characters." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380270928, 1);", connection, transaction) { CommandTimeout = GetCommandTimeout() })
            await gate.ExecuteNonQueryAsync(ct);

        const string familySql = """
        SELECT CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" p WHERE p."baseRecipeId" = r."id")
                    THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END
        FROM public."Recipe" r WHERE r."id" = @recipe_id FOR UPDATE OF r;
        """;

        int familyId;

        await using (var recipe = new NpgsqlCommand(familySql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            recipe.Parameters.AddWithValue("recipe_id", recipeId);

            if (await recipe.ExecuteScalarAsync(ct) is not int id)
                return new() { Status = RecipeChangeStatus.NotFound, Error = "The recipe no longer exists. Press Refresh." };

            familyId = id;
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
            return new() { Status = RecipeChangeStatus.Conflict, Error = "Recipe stages were changed by another client. Press Refresh before adding a stage." };

        if (stages.Any(stage => stage.Number <= 0) || stages.Select(stage => stage.Number).Distinct().Count() != stages.Count)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Existing stage numbers must be positive and unique." };

        var insertIndex = request.InsertBeforeStageId is int beforeId ? stages.FindIndex(stage => stage.Id == beforeId) : stages.Count;

        if (insertIndex < 0)
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The target row no longer exists in this recipe. Press Refresh." };

        var lastNumber = stages.Count == 0 ? 0 : stages.Max(stage => stage.Number);

        if (lastNumber == int.MaxValue || (insertIndex < stages.Count && (long)lastNumber + stages.Count + 2 > int.MaxValue))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Stage numbers are too large to add and renumber safely." };

        if (request.AutoAssignTagPrefix)
            prefix = await ResolveAutomaticStagePrefixAsync(connection, transaction, recipeId, familyId, request.RecipePageId, ct);

        // Блокируем шаблон до завершения создания стадии и параметров.
        await using (var template = new NpgsqlCommand("SELECT \"id\" FROM public.\"BaseRecipeStage\" WHERE \"id\" = @id AND \"baseRecipeId\" = @family_id FOR UPDATE;", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            template.Parameters.AddWithValue("id", request.BaseStageId);
            template.Parameters.AddWithValue("family_id", familyId);

            if (await template.ExecuteScalarAsync(ct) is null)
                return new() { Status = RecipeChangeStatus.NotFound, Error = "The stage template no longer exists in this recipe family. Press Refresh." };
        }

        var link = await FindTemplateParameterLinkAsync(connection, transaction, ct);
        var parameterIds = new HashSet<int>();

        // Все параметры шаблона должны принадлежать этому же семейству.
        var parametersSql = $"""
        SELECT p."id", p."kind", p."baseRecipeId"
        FROM {link.Table} l JOIN public."BaseParameter" p ON p."id" = l.{link.ParameterColumn}
        WHERE l.{link.StageColumn} = @template_id
        ORDER BY p."id" FOR SHARE OF l, p;
        """;

        await using (var parameters = new NpgsqlCommand(parametersSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            parameters.Parameters.AddWithValue("template_id", request.BaseStageId);

            await using var reader = await parameters.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(2) || reader.GetInt32(2) != familyId)
                    return new() { Status = RecipeChangeStatus.Invalid, Error = "The template contains a parameter outside this recipe family. Correct the parameter family before adding this stage." };

                if (!parameterIds.Add(reader.GetInt32(0)))
                    return new() { Status = RecipeChangeStatus.Invalid, Error = "The template contains duplicate parameter links." };

                var kind = reader.GetInt32(1);

                if (kind != (int)RecipeParameterKind.InputField && kind != (int)RecipeParameterKind.CheckBox)
                    return new() { Status = RecipeChangeStatus.Invalid, Error = "The template contains an unsupported parameter type." };
            }
        }

        const string insertStageSql = """
        INSERT INTO public."RecipeStage" ("stageNumb", "name", "auto", "description", "recipeId", "baseRecipeStageId")
        SELECT @number, b."name", @auto, @prefix, @recipe_id, b."id"
        FROM public."BaseRecipeStage" b WHERE b."id" = @template_id
        RETURNING "id";
        """;

        int stageId;

        await using (var insert = new NpgsqlCommand(insertStageSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            insert.Parameters.AddWithValue("number", lastNumber + 1);
            insert.Parameters.AddWithValue("auto", request.Auto);
            insert.Parameters.AddWithValue("prefix", prefix);
            insert.Parameters.AddWithValue("recipe_id", recipeId);
            insert.Parameters.AddWithValue("template_id", request.BaseStageId);

            stageId = (int)(await insert.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Stage creation returned no ID."));
        }

        var insertParametersSql = $"""
        INSERT INTO public."RecipeParameter" ("name", "value", "description", "active", "recipeStageId", "baseParameterId")
        SELECT p."name", 0.0, p."description", true, @stage_id, p."id"
        FROM {link.Table} l JOIN public."BaseParameter" p ON p."id" = l.{link.ParameterColumn}
        WHERE l.{link.StageColumn} = @template_id;
        """;

        await using (var insert = new NpgsqlCommand(insertParametersSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            insert.Parameters.AddWithValue("stage_id", stageId);
            insert.Parameters.AddWithValue("template_id", request.BaseStageId);

            if (await insert.ExecuteNonQueryAsync(ct) != parameterIds.Count)
                throw new InvalidOperationException("The template parameter set changed during stage creation.");
        }

        if (insertIndex < stages.Count)
        {
            stages.Insert(insertIndex, new() { Id = stageId, Number = lastNumber + 1, Auto = request.Auto });
            await RenumberRecipeStagesAsync(connection, transaction, recipeId, stages, lastNumber + 1, ct);
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, SelectedStageId = stageId, TagPrefix = prefix };
    }

    /// <summary>
    /// Находит промежуточную таблицу EF по двум внешним ключам, без угадывания её имени.
    /// Имена возвращаются PostgreSQL уже экранированными как SQL-идентификаторы.
    /// </summary>
    private async Task<(string Table, string StageColumn, string ParameterColumn)> FindTemplateParameterLinkAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        const string sql = """
        SELECT DISTINCT format('%I.%I', ns.nspname, t.relname), format('%I', sc.attname), format('%I', pc.attname)
        FROM pg_catalog.pg_constraint sf
        JOIN pg_catalog.pg_constraint pf ON pf.conrelid = sf.conrelid
        JOIN pg_catalog.pg_class t ON t.oid = sf.conrelid
        JOIN pg_catalog.pg_namespace ns ON ns.oid = t.relnamespace
        JOIN pg_catalog.pg_attribute sc ON sc.attrelid = sf.conrelid AND sc.attnum = sf.conkey[1]
        JOIN pg_catalog.pg_attribute pc ON pc.attrelid = pf.conrelid AND pc.attnum = pf.conkey[1]
        JOIN pg_catalog.pg_attribute sk ON sk.attrelid = sf.confrelid AND sk.attnum = sf.confkey[1]
        JOIN pg_catalog.pg_attribute pk ON pk.attrelid = pf.confrelid AND pk.attnum = pf.confkey[1]
        WHERE sf.contype = 'f' AND pf.contype = 'f' AND ns.nspname = 'public'
          AND sf.confrelid = 'public."BaseRecipeStage"'::regclass
          AND pf.confrelid = 'public."BaseParameter"'::regclass
          AND cardinality(sf.conkey) = 1 AND cardinality(pf.conkey) = 1
          AND cardinality(sf.confkey) = 1 AND cardinality(pf.confkey) = 1
          AND sk.attname = 'id' AND pk.attname = 'id';
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The BaseRecipeStage/BaseParameter relation table was not found by its foreign keys.");

        var result = (Table: reader.GetString(0), StageColumn: reader.GetString(1), ParameterColumn: reader.GetString(2));

        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Multiple BaseRecipeStage/BaseParameter relation tables were found. An explicit mapping is required.");

        return result;
    }
}