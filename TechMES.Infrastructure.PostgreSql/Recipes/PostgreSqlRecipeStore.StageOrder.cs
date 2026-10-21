using Npgsql;
using NpgsqlTypes;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    /// <summary>Записывает порядок 1..N без изменения ID и параметров стадий.</summary>
    private async Task RenumberRecipeStagesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int recipeId, IReadOnlyList<RecipeStagePositionDto> stages, int maxNumber, CancellationToken ct)
    {
        if (stages.Count == 0) return;

        if ((long)maxNumber + stages.Count > int.MaxValue)
            throw new InvalidOperationException("Stage numbers exceed the supported range.");

        const string sql = """
        UPDATE public."RecipeStage" s SET "stageNumb" = n.number
        FROM unnest(@ids::integer[], @numbers::integer[]) AS n(id, number)
        WHERE s."id" = n.id AND s."recipeId" = @recipe_id;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("recipe_id", recipeId);
        command.Parameters.AddWithValue("ids", stages.Select(stage => stage.Id).ToArray());

        // Временные номера не пересекаются с существующими.
        command.Parameters.AddWithValue("numbers", Enumerable.Range(maxNumber + 1, stages.Count).ToArray());

        if (await command.ExecuteNonQueryAsync(ct) != stages.Count)
            throw new InvalidOperationException("The recipe stage set changed while renumbering.");

        command.Parameters["numbers"].Value = Enumerable.Range(1, stages.Count).ToArray();

        if (await command.ExecuteNonQueryAsync(ct) != stages.Count)
            throw new InvalidOperationException("The recipe stage set changed while renumbering.");
    }

    /// <summary>Проверяет prefix без построения тега из имени Unit или кода шаблона.</summary>
    private static bool IsValidStagePrefix(string value) => value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);

    /// <summary>Читает до двух разных prefix, чтобы обнаружить неоднозначность.</summary>
    private async Task<List<string>> ReadStagePrefixesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int recipeId, CancellationToken ct)
    {
        const string sql = """
        SELECT DISTINCT btrim("description") AS prefix
        FROM public."RecipeStage"
        WHERE "recipeId" = @recipe_id AND btrim(COALESCE("description", '')) <> ''
        ORDER BY prefix LIMIT 2;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("recipe_id", recipeId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>();

        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));

        return result;
    }

    /// <summary>Использует prefix рецепта, затем текущего рецепта того же Unit и семейства.</summary>
    private async Task<string> ResolveAutomaticStagePrefixAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int recipeId, int familyId, int? pageId, CancellationToken ct)
    {
        var prefixes = await ReadStagePrefixesAsync(connection, transaction, recipeId, ct);

        if (prefixes.Count > 0)
            return prefixes.Count == 1 && IsValidStagePrefix(prefixes[0]) ? prefixes[0] : "";

        // Базовому рецепту нельзя назначать рабочий prefix выбранного Unit.
        if (recipeId == familyId) return "";

        const string sql = """
        SELECT DISTINCT p."recipeId"
        FROM public."RecipePage" p JOIN public."Recipe" r ON r."id" = p."recipeId"
        WHERE p."baseRecipeId" = @family_id AND (r."id" = @family_id OR r."baseRecipeId" = @family_id)
          AND ((@page_id IS NOT NULL AND p."id" = @page_id) OR (@page_id IS NULL AND p."recipeId" = @recipe_id))
        ORDER BY p."recipeId" LIMIT 2;
        """;

        var sources = new List<int>();

        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("family_id", familyId);
            command.Parameters.AddWithValue("recipe_id", recipeId);
            command.Parameters.AddWithValue("page_id", NpgsqlDbType.Integer, (object?)pageId ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct)) sources.Add(reader.GetInt32(0));
        }

        if (sources.Count != 1 || sources[0] == recipeId) return "";

        prefixes = await ReadStagePrefixesAsync(connection, transaction, sources[0], ct);

        // Пустой результат будет показан в WEB как предупреждение.
        return prefixes.Count == 1 && IsValidStagePrefix(prefixes[0]) ? prefixes[0] : "";
    }
}