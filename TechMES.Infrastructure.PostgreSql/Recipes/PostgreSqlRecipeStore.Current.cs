using Npgsql;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>Проверяет сохранённый рецепт, загружает PLC и затем фиксирует Current.</summary>
    public async Task<RecipeChangeResult> ActivateAsync(RecipeChangeRequest request, Func<RecipePlcUpload, CancellationToken, Task> upload, CancellationToken ct = default)
    {
        if (request.Operation != RecipeChangeOperation.SetCurrent || request.SourceRecipeId <= 0 || !ValidEditorShape(request.ExpectedSnapshot) || request.ExpectedSnapshot!.Recipe.Id != request.SourceRecipeId || request.ExpectedPages is not { Count: 1 } || request.ExpectedPages[0] is null)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Invalid Current recipe assignment. Press Refresh." };

        var expected = request.ExpectedPages[0];

        if (expected.Id <= 0 || expected.RecipeId <= 0 || expected.BaseRecipeId <= 0 || string.IsNullOrWhiteSpace(expected.Page) || request.ExpectedBaseRecipeId != expected.BaseRecipeId)
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Select a Unit with a valid recipe family." };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Согласуемся с редакторами; справочники и снимок кандидата сохраняем неизменными до commit.
        const string lockSql = """
        SELECT pg_advisory_xact_lock(1380270928, 1);
        LOCK TABLE public."BaseParameter", public."BaseRecipeStage" IN SHARE MODE;
        SELECT p."id" FROM public."RecipeParameter" p JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
        WHERE s."recipeId" = @recipe_id ORDER BY p."id" FOR SHARE OF p;
        SELECT "id" FROM public."RecipeStage" WHERE "recipeId" = @recipe_id ORDER BY "id" FOR SHARE;
        SELECT "id" FROM public."Recipe" WHERE "id" = @recipe_id FOR SHARE;
        """;

        await using (var command = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("recipe_id", request.SourceRecipeId);
            await command.ExecuteNonQueryAsync(ct);
        }

        RecipePageDto page;

        await using (var command = new NpgsqlCommand("SELECT \"id\", \"page\", \"recipeId\", \"baseRecipeId\", \"currentStageTag\", \"modeTag\" FROM public.\"RecipePage\" WHERE \"id\" = @id FOR UPDATE;", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", expected.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return new() { Status = RecipeChangeStatus.NotFound, Error = "The Unit no longer exists. Press Refresh." };

            page = new() { Id = reader.GetInt32(0), Page = ReadText(reader, 1), RecipeId = reader.GetInt32(2), BaseRecipeId = reader.GetInt32(3), CurrentStageTag = ReadText(reader, 4), ModeTag = ReadText(reader, 5) };
        }

        if (page.Page != expected.Page || page.RecipeId != expected.RecipeId || page.BaseRecipeId != expected.BaseRecipeId || page.CurrentStageTag != expected.CurrentStageTag || page.ModeTag != expected.ModeTag)
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The Unit assignment or control tags changed. Press Refresh before assigning." };

        var candidate = await ReadRecipeSnapshotAsync(connection, transaction, request.SourceRecipeId, ct);

        if (candidate is null)
            return new() { Status = RecipeChangeStatus.NotFound, Error = "The recipe no longer exists. Press Refresh." };

        if (EditorStamp(candidate) != EditorStamp(request.ExpectedSnapshot))
            return new() { Status = RecipeChangeStatus.Conflict, Error = "The selected recipe changed. Press Refresh before assigning it as Current." };

        // Повторяем отбор комбобокса: семейство и имя выбранного Unit.
        if (candidate.Recipe.Id == page.BaseRecipeId || candidate.Recipe.BaseRecipeId != page.BaseRecipeId || !candidate.Recipe.Name.Contains(page.Page, StringComparison.OrdinalIgnoreCase))
            return new() { Status = RecipeChangeStatus.Invalid, Error = "Select a working recipe for this Unit and its family." };

        await using (var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM public.\"RecipePage\" WHERE \"baseRecipeId\" = @id);", connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", candidate.Recipe.Id);

            if (await command.ExecuteScalarAsync(ct) is true)
                return new() { Status = RecipeChangeStatus.Invalid, Error = "An assigned base recipe cannot be selected as Current." };
        }

        // Справочники уже заблокированы; читаем коды и ВСЕ параметры семейства.
        var parameters = new List<RecipeColumnDto>();
        var codes = new Dictionary<int, string>();

        const string metadataSql = """
        SELECT "id", "tag", "kind" FROM public."BaseParameter" WHERE "baseRecipeId" = @family ORDER BY "id";
        SELECT "id", "description" FROM public."BaseRecipeStage" WHERE "baseRecipeId" = @family ORDER BY "id";
        """;

        await using (var command = new NpgsqlCommand(metadataSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("family", page.BaseRecipeId);
            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                parameters.Add(new() { Id = reader.GetInt32(0), TagSuffix = ReadText(reader, 1), Kind = (RecipeParameterKind)reader.GetInt32(2) });

            await reader.NextResultAsync(ct);

            while (await reader.ReadAsync(ct))
                codes.Add(reader.GetInt32(0), ReadText(reader, 1).Trim());
        }

        var prefixes = await ReadStagePrefixesAsync(connection, transaction, page.RecipeId, ct);
        var plan = new RecipePlcUpload(candidate, page, parameters, codes, prefixes);

        // Передаём канонические данные БД, а не присланные WEB значения.
        // Блокировки удерживаются до завершения загрузки PLC.
        await upload(plan, ct);

        const string assignSql = """
        UPDATE public."RecipePage" SET "recipeId" = @recipe_id
        WHERE "id" = @page_id AND "recipeId" = @expected_current AND "baseRecipeId" = @family;
        """;

        await using (var command = new NpgsqlCommand(assignSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("recipe_id", candidate.Recipe.Id);
            command.Parameters.AddWithValue("page_id", page.Id);
            command.Parameters.AddWithValue("expected_current", expected.RecipeId);
            command.Parameters.AddWithValue("family", page.BaseRecipeId);

            if (await command.ExecuteNonQueryAsync(ct) != 1)
                return new() { Status = RecipeChangeStatus.Conflict, Error = "The Unit assignment changed during saving. Press Refresh." };
        }

        await transaction.CommitAsync(ct);

        return new() { Status = RecipeChangeStatus.Saved, RecipeId = candidate.Recipe.Id };
    }
}