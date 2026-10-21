using System.Text.Json;
using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    private sealed record EditorTemplate(string Name, Dictionary<int, RecipeColumnDto> Parameters);

    /// <summary>Сравнивает снимки независимо от порядка ключей словарей.</summary>
    private static string EditorStamp(RecipeSnapshotDto value) => JsonSerializer.Serialize(new
    {
        value.Recipe,
        Columns = value.Columns.OrderBy(item => item.Id),
        Stages = value.Stages.OrderBy(item => item.Number).ThenBy(item => item.Id).Select(item => new { item.Id, item.Number, item.Name, item.Auto, item.BaseStageId, item.TagPrefix, Parameters = item.Parameters.OrderBy(pair => pair.Key) })
    });

    /// <summary>Проверяет структуру входного снимка до обращения к его коллекциям.</summary>
    private static bool ValidEditorShape(RecipeSnapshotDto? value) => value?.Recipe is not null && value.Columns is not null && value.Stages is not null && value.Columns.All(item => item is not null) && value.Stages.All(item => item is not null && item.Parameters is not null && item.Parameters.All(pair => pair.Value is not null && double.IsFinite(pair.Value.Value)));

    /// <summary>Сохраняет порядок, состав, значения и имя рецепта одной транзакцией.</summary>
    public async Task<RecipeEditorSaveResult> SaveEditorAsync(int recipeId, RecipeEditorSaveRequest request, CancellationToken ct = default)
    {
        if (recipeId <= 0 || !ValidEditorShape(request.Original) || !ValidEditorShape(request.Value) || request.Original.Recipe.Id != recipeId || request.Value.Recipe.Id != recipeId || request.RecipePageId is <= 0)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid recipe draft." };

        var draft = request.Value;

        if (draft.Stages.Any(item => item.Id == 0) || draft.Stages.Select(item => item.Id).Distinct().Count() != draft.Stages.Count || draft.Columns.Select(item => item.Id).Distinct().Count() != draft.Columns.Count)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Duplicate or invalid draft identities." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var link = await FindTemplateParameterLinkAsync(connection, transaction, ct);

        // Справочники стабилизируем, значения блокируем перед стадиями.
        var lockSql = $"""
        SELECT pg_advisory_xact_lock(1380270928, 1);
        LOCK TABLE public."RecipePage", public."BaseRecipeStage", public."BaseParameter", {link.Table} IN SHARE MODE;
        SELECT p."id" FROM public."RecipeParameter" p JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
        WHERE s."recipeId" = @id ORDER BY p."id" FOR UPDATE OF p;
        SELECT "id" FROM public."RecipeStage" WHERE "recipeId" = @id ORDER BY "id" FOR UPDATE;
        SELECT "id" FROM public."Recipe" WHERE "id" = @id FOR UPDATE;
        """;

        await using (var command = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", recipeId);
            await command.ExecuteNonQueryAsync(ct);
        }

        var current = await ReadRecipeSnapshotAsync(connection, transaction, recipeId, ct);

        if (current is null)
            return new() { Status = RecipeChangeStatus.NotFound, Error = "The recipe no longer exists." };

        if (EditorStamp(current) != EditorStamp(request.Original))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The recipe or its parameter definitions changed. Refresh before saving." };

        if (draft.Recipe.Number != current.Recipe.Number || draft.Recipe.BaseRecipeId != current.Recipe.BaseRecipeId)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "The recipe family cannot be changed here." };

        const string familySql = """
        SELECT CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" WHERE "baseRecipeId" = @id)
                    THEN @id ELSE COALESCE("baseRecipeId", "id") END
        FROM public."Recipe" WHERE "id" = @id;
        """;

        int familyId;

        await using (var command = new NpgsqlCommand(familySql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", recipeId);
            familyId = (int)(await command.ExecuteScalarAsync(ct))!;
        }

        var templates = await ReadEditorTemplatesAsync(connection, transaction, link, familyId, draft.Stages.Where(item => item.Id < 0).Select(item => item.BaseStageId).Distinct().ToArray(), ct);
        var old = current.Stages.ToDictionary(item => item.Id);
        var columns = current.Columns.ToDictionary(item => item.Id);

        foreach (var template in templates.Values)
            foreach (var column in template.Parameters.Values)
                columns[column.Id] = column;

        var usedColumns = draft.Stages.SelectMany(item => item.Parameters.Keys).ToHashSet();

        if (!usedColumns.SetEquals(draft.Columns.Select(item => item.Id)) || draft.Columns.Any(item => !columns.TryGetValue(item.Id, out var actual) || JsonSerializer.Serialize(item) != JsonSerializer.Serialize(actual)))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "Parameter definitions changed. Refresh before saving." };

        foreach (var stage in draft.Stages)
        {
            if (stage.Id > 0)
            {
                if (!old.TryGetValue(stage.Id, out var previous) || stage.Name != previous.Name || stage.BaseStageId != previous.BaseStageId || stage.Auto != previous.Auto || stage.TagPrefix != previous.TagPrefix || !stage.Parameters.Keys.ToHashSet().SetEquals(previous.Parameters.Keys) || stage.Parameters.Any(pair => pair.Value.Id != previous.Parameters[pair.Key].Id || pair.Value.Active != previous.Parameters[pair.Key].Active))
                    return new() { Status = RecipeChangeStatus.Invalid, Error = "An existing stage structure was changed unexpectedly." };
            }
            else if (!templates.TryGetValue(stage.BaseStageId, out var template) || stage.Name != template.Name || !stage.Parameters.Keys.ToHashSet().SetEquals(template.Parameters.Keys) || stage.Parameters.Any(pair => pair.Value.Id != 0 || !pair.Value.Active) || stage.Auto)
            {
                return new() { Status = RecipeChangeStatus.Conflict, Error = "A stage template changed or belongs to another family. Refresh before saving." };
            }

            if (stage.Parameters.Any(pair => columns[pair.Key].Kind == RecipeParameterKind.CheckBox && pair.Value.Value != 0 && pair.Value.Value != 1))
                return new() { Status = RecipeChangeStatus.Invalid, Error = "Checkbox values must be 0 or 1." };
        }

        var name = draft.Recipe.Name == current.Recipe.Name ? current.Recipe.Name : (draft.Recipe.Name ?? "").Trim();

        if (string.IsNullOrWhiteSpace(name))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Enter a recipe name." };

        if (name != current.Recipe.Name)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM public.\"Recipe\" WHERE lower(btrim(\"name\")) = lower(@name) AND \"id\" <> @id);", connection, transaction) { CommandTimeout = GetCommandTimeout() };
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("id", recipeId);

            if (await command.ExecuteScalarAsync(ct) is true)
                return new() { Status = RecipeChangeStatus.Conflict, Error = "A recipe with this name already exists." };
        }

        var maxNumber = current.Stages.Count == 0 ? 0 : current.Stages.Max(item => item.Number);

        if (current.Stages.Any(item => item.Number <= 0) || current.Stages.Select(item => item.Number).Distinct().Count() != current.Stages.Count)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Saved stage numbers must be positive and unique." };

        var reserve = Math.Max(maxNumber, draft.Stages.Count);

        if ((long)reserve + current.Stages.Count > int.MaxValue)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Stage numbers exceed the supported range." };

        var prefix = await ResolveAutomaticStagePrefixAsync(connection, transaction, recipeId, familyId, request.RecipePageId, ct);

        // Освобождаем конечные номера до вставки новых стадий.
        if (current.Stages.Count > 0)
        {
            await using var command = new NpgsqlCommand("UPDATE public.\"RecipeStage\" s SET \"stageNumb\" = n.number FROM unnest(@ids::integer[], @numbers::integer[]) n(id, number) WHERE s.\"id\" = n.id AND s.\"recipeId\" = @id;", connection, transaction) { CommandTimeout = GetCommandTimeout() };
            command.Parameters.AddWithValue("id", recipeId);
            command.Parameters.AddWithValue("ids", current.Stages.Select(item => item.Id).ToArray());
            command.Parameters.AddWithValue("numbers", Enumerable.Range(reserve + 1, current.Stages.Count).ToArray());

            await command.ExecuteNonQueryAsync(ct);
        }

        var kept = draft.Stages.Where(item => item.Id > 0).Select(item => item.Id).ToArray();

        const string deleteSql = """
        DELETE FROM public."RecipeParameter" p USING public."RecipeStage" s
        WHERE p."recipeStageId" = s."id" AND s."recipeId" = @id AND NOT (s."id" = ANY(@kept));
        DELETE FROM public."RecipeStage" WHERE "recipeId" = @id AND NOT ("id" = ANY(@kept));
        """;

        await using (var command = new NpgsqlCommand(deleteSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", recipeId);
            command.Parameters.AddWithValue("kept", kept);

            await command.ExecuteNonQueryAsync(ct);
        }

        // Порядок списка задаёт окончательные номера 1..N.
        foreach (var (stage, index) in draft.Stages.Select((stage, index) => (stage, index)))
        {
            var stageId = stage.Id;
            var isNew = stageId < 0;

            var sql = isNew ? "INSERT INTO public.\"RecipeStage\" (\"stageNumb\",\"name\",\"auto\",\"description\",\"recipeId\",\"baseRecipeStageId\") VALUES (@number,@name,false,@prefix,@recipe,@template) RETURNING \"id\";"
                : "UPDATE public.\"RecipeStage\" SET \"stageNumb\"=@number WHERE \"id\"=@stage AND \"recipeId\"=@recipe RETURNING \"id\";";

            await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
            {
                command.Parameters.AddWithValue("number", index + 1);
                command.Parameters.AddWithValue("recipe", recipeId);

                if (isNew)
                {
                    command.Parameters.AddWithValue("name", templates[stage.BaseStageId].Name);
                    command.Parameters.AddWithValue("prefix", prefix);
                    command.Parameters.AddWithValue("template", stage.BaseStageId);
                }
                else
                {
                    command.Parameters.AddWithValue("stage", stageId);
                }

                stageId = (int)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Stage save returned no ID."));
            }

            foreach (var pair in stage.Parameters)
            {
                if (!isNew && pair.Value.Value == old[stage.Id].Parameters[pair.Key].Value)
                    continue;

                var column = columns[pair.Key];

                sql = isNew ? "INSERT INTO public.\"RecipeParameter\" (\"name\",\"value\",\"description\",\"active\",\"recipeStageId\",\"baseParameterId\") VALUES (@name,@value,@units,true,@stage,@base);"
                    : "UPDATE public.\"RecipeParameter\" SET \"value\"=@value WHERE \"id\"=@id AND \"recipeStageId\"=@stage;";

                await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
                command.Parameters.AddWithValue("value", pair.Value.Value);
                command.Parameters.AddWithValue("stage", stageId);

                if (isNew)
                {
                    command.Parameters.AddWithValue("name", column.Name);
                    command.Parameters.AddWithValue("units", column.Description);
                    command.Parameters.AddWithValue("base", pair.Key);
                }
                else
                {
                    command.Parameters.AddWithValue("id", pair.Value.Id);
                }

                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Parameter save affected an unexpected number of rows.");
            }
        }

        await using (var command = new NpgsqlCommand("UPDATE public.\"Recipe\" SET \"name\"=@name WHERE \"id\"=@id;", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("id", recipeId);

            await command.ExecuteNonQueryAsync(ct);
        }

        var saved = await ReadRecipeSnapshotAsync(connection, transaction, recipeId, ct);

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, Snapshot = saved };
    }

    /// <summary>Читает актуальные состав и метаданные новых шаблонов выбранного семейства.</summary>
    private async Task<Dictionary<int, EditorTemplate>> ReadEditorTemplatesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, (string Table, string StageColumn, string ParameterColumn) link, int familyId, int[] ids, CancellationToken ct)
    {
        var sql = $"""
        SELECT s."id", s."name", p."id", p."name", p."description", p."type", p."tag", p."kind", p."format", p."baseRecipeId"
        FROM public."BaseRecipeStage" s
        LEFT JOIN {link.Table} l ON l.{link.StageColumn} = s."id"
        LEFT JOIN public."BaseParameter" p ON p."id" = l.{link.ParameterColumn}
        WHERE s."baseRecipeId" = @family AND s."id" = ANY(@ids)
        ORDER BY s."id", p."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("family", familyId);
        command.Parameters.AddWithValue("ids", ids);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<int, EditorTemplate>();

        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);

            if (!result.TryGetValue(id, out var template))
                result[id] = template = new(ReadText(reader, 1), []);

            if (reader.IsDBNull(2))
                continue;

            if (reader.IsDBNull(9) || reader.GetInt32(9) != familyId)
                throw new InvalidOperationException("A template parameter belongs to another family.");

            var column = new RecipeColumnDto { Id = reader.GetInt32(2), Name = ReadText(reader, 3), Description = ReadText(reader, 4), Order = reader.GetInt32(5), TagSuffix = ReadText(reader, 6), Kind = (RecipeParameterKind)reader.GetInt32(7), Format = (RecipeParameterFormat)reader.GetInt32(8) };

            if (!template.Parameters.TryAdd(column.Id, column))
                throw new InvalidOperationException("Duplicate template parameter.");
        }

        return result;
    }
}