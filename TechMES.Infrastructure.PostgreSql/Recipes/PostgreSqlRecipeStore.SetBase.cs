using System.Text.Json;
using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>Сравнивает привязки Unit независимо от порядка строк.</summary>
    private static string BasePagesStamp(IEnumerable<RecipePageDto> pages) => JsonSerializer.Serialize(pages.OrderBy(page => page.Id).Select(page => new { page.Id, page.Page, page.RecipeId, page.BaseRecipeId }));

    /// <summary>Назначает новый базовый рецепт, сохраняя всё семейство одной транзакцией.</summary>
    private async Task<RecipeChangeResult> SetBaseRecipeAsync(RecipeChangeRequest request, CancellationToken ct)
    {
        if (request.SourceRecipeId <= 0 || request.ExpectedBaseRecipeId is not int familyId || familyId <= 0 || request.SourceRecipeId == familyId || !ValidEditorShape(request.ExpectedSnapshot) || request.ExpectedSnapshot!.Recipe.Id != request.SourceRecipeId || request.ExpectedPages is null || request.ExpectedPages.Count == 0 || request.ExpectedPages.Any(page => page is null || page.Id <= 0 || page.BaseRecipeId != familyId) || request.ExpectedPages.Select(page => page.Id).Distinct().Count() != request.ExpectedPages.Count)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid base recipe assignment. Press Refresh." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Согласуем операцию с редакторами и защищаем привязки от внешних изменений.
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
        SELECT EXISTS (SELECT 1 FROM public."RecipePage" WHERE "recipeId" = @id OR "baseRecipeId" = @id),
               EXISTS (SELECT 1 FROM public."BaseParameter" WHERE "baseRecipeId" = @id)
               OR EXISTS (SELECT 1 FROM public."BaseRecipeStage" WHERE "baseRecipeId" = @id)
               OR EXISTS (SELECT 1 FROM public."Recipe" WHERE "baseRecipeId" = @id AND "id" <> @id);
        """;

        await using (var command = new NpgsqlCommand(usageSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", request.SourceRecipeId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            if (reader.GetBoolean(0))
                return new() { Status = RecipeChangeStatus.Conflict, Error = "This recipe is already Current or assigned as base. Press Refresh." };

            if (reader.GetBoolean(1))
                return new() { Status = RecipeChangeStatus.Conflict, Error = "The selected recipe already owns family data. It cannot replace another family's base recipe." };
        }

        if (current.Recipe.BaseRecipeId != familyId || EditorStamp(current) != EditorStamp(request.ExpectedSnapshot))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The recipe or its family changed. Press Refresh before assigning it as base." };

        var pages = new List<RecipePageDto>();

        await using (var command = new NpgsqlCommand("SELECT \"id\", \"page\", \"recipeId\", \"baseRecipeId\" FROM public.\"RecipePage\" WHERE \"baseRecipeId\" = @family ORDER BY \"id\";", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("family", familyId);

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                pages.Add(new() { Id = reader.GetInt32(0), Page = ReadText(reader, 1), RecipeId = reader.GetInt32(2), BaseRecipeId = reader.GetInt32(3) });
        }

        if (BasePagesStamp(pages) != BasePagesStamp(request.ExpectedPages))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "Unit assignments changed after loading. Press Refresh." };

        // Кандидат должен содержать только стадии и параметры своего семейства.
        const string invalidSql = """
        SELECT EXISTS (
            SELECT 1 FROM public."RecipeStage" s LEFT JOIN public."BaseRecipeStage" b ON b."id" = s."baseRecipeStageId"
            WHERE s."recipeId" = @id AND (b."id" IS NULL OR b."baseRecipeId" IS DISTINCT FROM @family)
        ) OR EXISTS (
            SELECT 1 FROM public."RecipeParameter" p
            JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
            LEFT JOIN public."BaseParameter" b ON b."id" = p."baseParameterId"
            WHERE s."recipeId" = @id AND (b."id" IS NULL OR b."baseRecipeId" IS DISTINCT FROM @family)
        );
        """;

        await using (var command = new NpgsqlCommand(invalidSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", request.SourceRecipeId);
            command.Parameters.AddWithValue("family", familyId);

            if (await command.ExecuteScalarAsync(ct) is true)
                return new() { Status = RecipeChangeStatus.Invalid, Error = "The recipe contains stages or parameters outside its family." };
        }

        // Меняются только ссылки на корень семейства. ID, значения, состав и PLC tags сохраняются.
        // Другие назначенные базовые рецепты являются самостоятельными семействами.
        const string assignSql = """
        UPDATE public."Recipe" r SET "baseRecipeId" = @id
        WHERE r."id" = @family OR (r."baseRecipeId" = @family AND NOT EXISTS (
            SELECT 1 FROM public."RecipePage" p WHERE p."baseRecipeId" = r."id"
        ));
        UPDATE public."BaseParameter" SET "baseRecipeId" = @id WHERE "baseRecipeId" = @family;
        UPDATE public."BaseRecipeStage" SET "baseRecipeId" = @id WHERE "baseRecipeId" = @family;
        UPDATE public."RecipePage" SET "baseRecipeId" = @id WHERE "baseRecipeId" = @family;
        """;

        await using (var command = new NpgsqlCommand(assignSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", request.SourceRecipeId);
            command.Parameters.AddWithValue("family", familyId);

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, RecipeId = request.SourceRecipeId };
    }
}