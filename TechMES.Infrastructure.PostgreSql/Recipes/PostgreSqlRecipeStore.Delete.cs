using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>Удаляет рецепт с его экземплярами стадий, сохраняя справочники семейства.</summary>
    private async Task<RecipeChangeResult> DeleteRecipeAsync(RecipeChangeRequest request, CancellationToken ct)
    {
        if (request.SourceRecipeId <= 0 || !ValidEditorShape(request.ExpectedSnapshot) || request.ExpectedSnapshot!.Recipe.Id != request.SourceRecipeId)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Refresh the recipe before deleting it." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Удаление выполняется редко. На время проверки запрещаем менять Current,
        // зависимости семейства и содержимое рецепта, в том числе внешним клиентам.
        const string lockSql = """
        SELECT pg_advisory_xact_lock(1380270928, 1);
        LOCK TABLE public."RecipePage", public."BaseRecipeStage", public."BaseParameter",
                   public."RecipeParameter", public."RecipeStage", public."Recipe" IN SHARE ROW EXCLUSIVE MODE;
        """;

        await using (var command = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
            await command.ExecuteNonQueryAsync(ct);

        var current = await ReadRecipeSnapshotAsync(connection, transaction, request.SourceRecipeId, ct);

        if (current is null)
            return new() { Status = RecipeChangeStatus.NotFound, Error = "The recipe no longer exists. Press Refresh." };

        const string usageSql = """
        SELECT EXISTS (SELECT 1 FROM public."RecipePage" WHERE "recipeId" = @id),
               EXISTS (SELECT 1 FROM public."RecipePage" WHERE "baseRecipeId" = @id)
               OR EXISTS (SELECT 1 FROM public."BaseRecipeStage" WHERE "baseRecipeId" = @id)
               OR EXISTS (SELECT 1 FROM public."BaseParameter" WHERE "baseRecipeId" = @id)
               OR EXISTS (SELECT 1 FROM public."Recipe" WHERE "baseRecipeId" = @id AND "id" <> @id);
        """;

        await using (var command = new NpgsqlCommand(usageSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", request.SourceRecipeId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            if (reader.GetBoolean(0))
                return new() { Status = RecipeChangeStatus.Conflict, Error = "This is a Current recipe for a Unit. Assign another recipe before deleting it." };

            if (reader.GetBoolean(1))
                return new() { Status = RecipeChangeStatus.Conflict, Error = "This base recipe is used by Units, parameters, stage templates or other recipes. It cannot be deleted." };
        }

        if (EditorStamp(current) != EditorStamp(request.ExpectedSnapshot))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The recipe changed after loading. Press Refresh before deleting it." };

        const string deleteSql = """
        DELETE FROM public."RecipeParameter" p USING public."RecipeStage" s
        WHERE p."recipeStageId" = s."id" AND s."recipeId" = @id;
        DELETE FROM public."RecipeStage" WHERE "recipeId" = @id;
        DELETE FROM public."Recipe" WHERE "id" = @id;
        """;

        await using (var command = new NpgsqlCommand(deleteSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", request.SourceRecipeId);

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, RecipeId = request.SourceRecipeId };
    }
}