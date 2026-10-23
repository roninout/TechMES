using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>Обновляет только RecipeParameter.value; состав, порядок и Current сохраняются.</summary>
    public async Task<RecipeEditorSaveResult> SaveValuesAsync(int recipeId, RecipeValuesSaveRequest request, CancellationToken ct = default)
    {
        if (recipeId <= 0 || !ValidEditorShape(request.Original) || request.Original.Recipe.Id != recipeId || request.Values is null || request.Values.Count == 0 || request.Values.Any(item => item is null || !double.IsFinite(item.Value)))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid recipe values." };

        var values = request.Values;

        if (values.Select(item => (item.StageId, item.ColumnId)).Distinct().Count() != values.Count)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Duplicate recipe cells." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Тот же порядок блокировок, что у редактора: параметры, стадии, рецепт.
        const string lockSql = """
        SELECT pg_advisory_xact_lock(1380270928, 1);
        LOCK TABLE public."BaseParameter" IN SHARE MODE;
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

        // Проверяем и структуру, и старые значения: чужие изменения не перезаписываем.
        if (EditorStamp(current) != EditorStamp(request.Original))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The recipe changed in the database. Press Refresh before saving." };

        var cells = current.Stages.SelectMany(stage => stage.Parameters.Select(pair => new { StageId = stage.Id, ColumnId = pair.Key, Parameter = pair.Value })).ToDictionary(item => (item.StageId, item.ColumnId));
        var columns = current.Columns.ToDictionary(item => item.Id);

        if (values.Count != cells.Count || values.Any(item => !cells.ContainsKey((item.StageId, item.ColumnId)) || !columns.TryGetValue(item.ColumnId, out var column) || !Enum.IsDefined(column.Kind) || (column.Kind == RecipeParameterKind.CheckBox && item.Value is not (0 or 1))))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "A complete snapshot with valid values for every recipe parameter is required." };

        // Один SQL-запрос обновляет все значения внутри общей транзакции.
        const string updateSql = """
        UPDATE public."RecipeParameter" p SET "value" = v.value
        FROM unnest(@ids::integer[], @values::double precision[]) AS v(id, value)
        JOIN public."RecipeStage" s ON s."recipeId" = @recipe_id
        WHERE p."id" = v.id AND p."recipeStageId" = s."id";
        """;

        await using (var command = new NpgsqlCommand(updateSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("recipe_id", recipeId);
            command.Parameters.AddWithValue("ids", values.Select(item => cells[(item.StageId, item.ColumnId)].Parameter.Id).ToArray());
            command.Parameters.AddWithValue("values", values.Select(item => item.Value).ToArray());

            if (await command.ExecuteNonQueryAsync(ct) != values.Count)
                throw new InvalidOperationException("Recipe value save affected an unexpected number of rows.");
        }

        var saved = await ReadRecipeSnapshotAsync(connection, transaction, recipeId, ct);
        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, Snapshot = saved };
    }
}