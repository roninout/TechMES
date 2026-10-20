using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    private const string FamilySql = """
    SELECT DISTINCT r."id", r."name" FROM public."RecipePage" p
    JOIN public."Recipe" r ON r."id" = p."baseRecipeId" ORDER BY r."id";
    """;

    private const string BaseParameterColumns = "\"id\", \"baseRecipeId\", \"type\", \"name\", \"description\", \"tag\", \"kind\", \"format\"";

    private sealed record ParameterStageUse(int ParameterId, int StageId, string Name, int? FamilyId, int? RecipeId, string RecipeName, int? RecipeFamilyId);
    private sealed record ParameterValueUse(int Id, int ParameterId, double Value, bool Active, int? StageFamilyId, int? RecipeId, string RecipeName, int? RecipeFamilyId, int? StageId, string StageName);
    private sealed record ParameterState(List<RecipeFamilyDto> Families, List<RecipeBaseParameterDto> Parameters, List<ParameterStageUse> Stages, List<ParameterValueUse> Values);
    private sealed record ParameterBatchResult(RecipeBaseParameterPreviewDto Preview, RecipeChangeStatus Status, int Synchronized);

    /// <summary>Читает каталог и признаки использования параметров в шаблонах и рецептах.</summary>
    public async Task<RecipeBaseParameterCatalogDto> GetBaseParametersAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var link = await FindTemplateParameterLinkAsync(connection, null, ct);

        var sql = FamilySql + $"""
        SELECT {BaseParameterColumns} FROM public."BaseParameter" ORDER BY "id";
        SELECT l.{link.ParameterColumn} FROM {link.Table} l
        UNION SELECT "baseParameterId" FROM public."RecipeParameter" WHERE "baseParameterId" IS NOT NULL;
        """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = GetCommandTimeout() };
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new RecipeBaseParameterCatalogDto();

        while (await reader.ReadAsync(ct)) result.Families.Add(new(reader.GetInt32(0), ReadText(reader, 1)));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) result.Parameters.Add(ReadBaseParameter(reader));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) result.UsedParameterIds.Add(reader.GetInt32(0));

        return result;
    }

    /// <summary>Предварительный просмотр не изменяет БД и не удерживает блокировки во время диалога.</summary>
    public async Task<RecipeBaseParameterPreviewDto> PreviewBaseParametersAsync(RecipeBaseParameterBatchRequest request, CancellationToken ct = default)
        => (await ProcessBaseParametersAsync(request, false, ct)).Preview;

    /// <summary>Применяет подтверждённые изменения, сохраняя значения параметров существующих рецептов.</summary>
    public async Task<RecipeBaseParameterSaveResult> SaveBaseParametersAsync(RecipeBaseParameterBatchRequest request, CancellationToken ct = default)
    {
        var result = await ProcessBaseParametersAsync(request, true, ct);

        return new()
        {
            Status = result.Status,
            ChangedParameters = result.Status == RecipeChangeStatus.Saved ? request.Changes.Count : 0,
            SynchronizedValues = result.Synchronized,
            Error = result.Preview.Errors.Count == 0 ? null : string.Join("\n", result.Preview.Errors)
        };
    }

    /// <summary>Проверяет пакет одного семейства, строит отчёт и при подтверждении выполняет запись.</summary>
    private async Task<ParameterBatchResult> ProcessBaseParametersAsync(RecipeBaseParameterBatchRequest request, bool save, CancellationToken ct)
    {
        var preview = new RecipeBaseParameterPreviewDto();

        if (request.FamilyId <= 0 || request.Changes is null || request.Changes.Count == 0 || request.Changes.Any(change => change is null || change.Value is null || change.Value.Id == 0 || (change.Delete && change.Value.Id < 0)) || request.Changes.Select(change => change.Value.Id).Distinct().Count() != request.Changes.Count)
        {
            preview.Errors.Add("No valid parameter changes were provided.");
            return new(preview, RecipeChangeStatus.Invalid, 0);
        }

        var changes = request.Changes.Select(change => change with { Value = change.Value with { Name = (change.Value.Name ?? "").Trim(), Tag = (change.Value.Tag ?? "").Trim(), Description = change.Value.Description ?? "" } }).OrderBy(change => change.Value.Id).ToList();
        var ids = changes.Where(change => change.Value.Id > 0).Select(change => change.Value.Id).ToArray();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(save ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.RepeatableRead, ct);
        var link = await FindTemplateParameterLinkAsync(connection, transaction, ct);

        if (save)
        {
            // Блокировки действуют только во время подтверждённой записи.
            // Сначала стабилизируем значения и стадии, затем определения параметров.
            var lockSql = $"""
            SELECT pg_advisory_xact_lock(1380270928, 1);
            LOCK TABLE public."RecipePage" IN SHARE MODE;
            LOCK TABLE public."BaseParameter" IN SHARE ROW EXCLUSIVE MODE;
            SELECT "id" FROM public."RecipeParameter" WHERE "baseParameterId" = ANY(@ids) ORDER BY "id" FOR UPDATE;
            SELECT s."id" FROM public."RecipeStage" s
            WHERE EXISTS (SELECT 1 FROM public."RecipeParameter" p WHERE p."recipeStageId" = s."id" AND p."baseParameterId" = ANY(@ids))
               OR EXISTS (SELECT 1 FROM {link.Table} l WHERE l.{link.StageColumn} = s."baseRecipeStageId" AND l.{link.ParameterColumn} = ANY(@ids))
            ORDER BY s."id" FOR UPDATE;
            SELECT b."id" FROM public."BaseRecipeStage" b WHERE EXISTS (SELECT 1 FROM {link.Table} l WHERE l.{link.StageColumn} = b."id" AND l.{link.ParameterColumn} = ANY(@ids)) ORDER BY b."id" FOR UPDATE;
            SELECT l.{link.StageColumn} FROM {link.Table} l WHERE l.{link.ParameterColumn} = ANY(@ids) ORDER BY l.{link.StageColumn}, l.{link.ParameterColumn} FOR SHARE;
            SELECT r."id" FROM public."Recipe" r ORDER BY r."id" FOR SHARE;
            SELECT "id" FROM public."BaseParameter" WHERE "baseRecipeId" = @family OR "id" = ANY(@ids) ORDER BY "id" FOR UPDATE;
            """;

            await using var gate = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
            gate.Parameters.AddWithValue("ids", ids);
            gate.Parameters.AddWithValue("family", request.FamilyId);

            await gate.ExecuteNonQueryAsync(ct);
        }

        var state = await ReadParameterStateAsync(connection, transaction, request.FamilyId, ids, link, ct);
        var conflict = false;

        if (state.Families.Count == 0) preview.Errors.Add("No base recipes are configured. Create a base recipe first.");
        if (!state.Families.Any(family => family.Id == request.FamilyId)) preview.Errors.Add("The selected family no longer exists. Reload the page.");

        foreach (var change in changes)
        {
            var value = change.Value;
            var old = state.Parameters.FirstOrDefault(parameter => parameter.Id == value.Id);
            var stageUses = state.Stages.Where(use => use.ParameterId == value.Id).ToList();
            var valueUses = state.Values.Where(use => use.ParameterId == value.Id).ToList();
            var family = state.Families.FirstOrDefault(item => item.Id == value.BaseRecipeId);

            var impact = new RecipeBaseParameterImpactDto
            {
                Name = value.Name,
                Action = change.Delete ? "Delete" : value.Id < 0 ? "Create" : "Update",
                Family = family?.Name ?? "Unassigned",
                ValuesCount = valueUses.Count
            };

            impact.Stages = stageUses.Select(use => $"Base stage [{use.StageId}] {use.Name}").Concat(valueUses.Where(use => use.StageId.HasValue).Select(use => $"Recipe stage [{use.StageId}] {use.StageName}")).Distinct().ToList();
            impact.Recipes = stageUses.Where(use => use.RecipeId.HasValue).Select(use => $"[{use.RecipeId}] {use.RecipeName}").Concat(valueUses.Where(use => use.RecipeId.HasValue).Select(use => $"[{use.RecipeId}] {use.RecipeName}")).Distinct().ToList();
            impact.Fields = change.Delete ? ["Delete this parameter definition."] : DescribeParameterChanges(change.Original, value);

            preview.Changes.Add(impact);

            if ((value.Id > 0 && (change.Original is null || change.Original.Id != value.Id || change.Original != old)) || (value.Id < 0 && change.Original is not null))
            {
                preview.Errors.Add($"{value.Name}: the original parameter changed or no longer exists. Reload the page.");
                conflict = true;
            }

            if (value.BaseRecipeId != request.FamilyId || (value.Id > 0 && old?.BaseRecipeId != request.FamilyId))
                preview.Errors.Add($"{value.Name}: changes are allowed only within the selected family.");

            if (change.Delete)
            {
                if (stageUses.Count > 0 || valueUses.Count > 0)
                    preview.Errors.Add($"{value.Name}: cannot delete. Used by {stageUses.Select(use => use.StageId).Distinct().Count()} base stages and {valueUses.Count} recipe parameter rows. See the usage lists.");

                continue;
            }

            if (family is null || value.Name.Length == 0 || value.Order < 0 || !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Format) || value.Tag.Any(char.IsWhiteSpace) || value.Tag.Any(char.IsControl))
                preview.Errors.Add($"{value.Name}: select an existing family, enter a name and check the order, tag, type and format.");

            if (stageUses.Any(use => use.FamilyId != value.BaseRecipeId || (use.RecipeId.HasValue && use.RecipeFamilyId != value.BaseRecipeId)) || valueUses.Any(use => (use.StageId.HasValue && use.StageFamilyId != value.BaseRecipeId) || (use.RecipeId.HasValue && use.RecipeFamilyId != value.BaseRecipeId)))
                preview.Errors.Add($"{value.Name}: the parameter is used by stages or recipes from another family. Remove those links before changing its family.");

            if (value.Kind == RecipeParameterKind.CheckBox && valueUses.Any(use => use.Value != 0 && use.Value != 1))
                preview.Errors.Add($"{value.Name}: existing recipe values must be 0 or 1 for the CheckBox type.");
        }

        // Проверяем итоговый набор семейства, включая неизменённые строки и новые параметры.
        var resulting = state.Parameters.Where(parameter => parameter.BaseRecipeId == request.FamilyId).ToDictionary(parameter => parameter.Id);

        foreach (var change in changes)
        {
            if (change.Delete) resulting.Remove(change.Value.Id);
            else resulting[change.Value.Id] = change.Value;
        }

        if (changes.Any(change => !change.Delete))
        {
            foreach (var duplicate in resulting.Values.GroupBy(parameter => parameter.Order).Where(group => group.Count() > 1))
                preview.Errors.Add($"Order {duplicate.Key} is already used in this family: {string.Join(", ", duplicate.Select(parameter => parameter.Name))}.");
        }

        // Подтверждение относится к конкретному пакету, определениям и зависимостям.
        var json = JsonSerializer.Serialize(new { request.FamilyId, Changes = changes, State = state }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        preview.Token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        if (preview.Errors.Count > 0) return new(preview, conflict ? RecipeChangeStatus.Conflict : RecipeChangeStatus.Invalid, 0);
        if (!save) return new(preview, RecipeChangeStatus.Saved, 0);

        if (!string.Equals(request.PreviewToken, preview.Token, StringComparison.Ordinal))
        {
            preview.Errors.Add("Parameter data or dependencies changed after the preview. Review the changes again.");
            return new(preview, RecipeChangeStatus.Conflict, 0);
        }

        var synchronized = 0;

        foreach (var change in changes)
        {
            var value = change.Value;

            if (change.Delete)
            {
                // Повторный NOT EXISTS защищает связанные данные даже при наличии CASCADE.
                var deleteSql = $"""
                DELETE FROM public."BaseParameter" b WHERE b."id" = @id AND b."baseRecipeId" = @family
                AND NOT EXISTS (SELECT 1 FROM {link.Table} l WHERE l.{link.ParameterColumn} = b."id")
                AND NOT EXISTS (SELECT 1 FROM public."RecipeParameter" p WHERE p."baseParameterId" = b."id");
                """;

                await using var delete = new NpgsqlCommand(deleteSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
                delete.Parameters.AddWithValue("id", value.Id);
                delete.Parameters.AddWithValue("family", request.FamilyId);

                if (await delete.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("The parameter could not be deleted. Reload its usage information.");

                continue;
            }

            var sql = value.Id < 0 ? """
            INSERT INTO public."BaseParameter" ("baseRecipeId", "type", "name", "description", "tag", "kind", "format")
            VALUES (@family, @order, @name, @description, @tag, @kind, @format);
            """ : """
            UPDATE public."BaseParameter" SET "baseRecipeId" = @family, "type" = @order, "name" = @name, "description" = @description, "tag" = @tag, "kind" = @kind, "format" = @format WHERE "id" = @id;
            """;

            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
            command.Parameters.AddWithValue("id", value.Id);
            command.Parameters.AddWithValue("family", value.BaseRecipeId!.Value);
            command.Parameters.AddWithValue("order", value.Order);
            command.Parameters.AddWithValue("name", value.Name);
            command.Parameters.AddWithValue("description", value.Description);
            command.Parameters.AddWithValue("tag", value.Tag);
            command.Parameters.AddWithValue("kind", (int)value.Kind);
            command.Parameters.AddWithValue("format", (int)value.Format);

            await command.ExecuteNonQueryAsync(ct);

            if (value.Id > 0)
            {
                await using var sync = new NpgsqlCommand("UPDATE public.\"RecipeParameter\" SET \"name\" = @name, \"description\" = @description WHERE \"baseParameterId\" = @id;", connection, transaction) { CommandTimeout = GetCommandTimeout() };
                sync.Parameters.AddWithValue("id", value.Id);
                sync.Parameters.AddWithValue("name", value.Name);
                sync.Parameters.AddWithValue("description", value.Description);

                synchronized += await sync.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);

        return new(preview, RecipeChangeStatus.Saved, synchronized);
    }

    /// <summary>Читает определения семейства и все места использования изменяемых параметров.</summary>
    private async Task<ParameterState> ReadParameterStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int familyId, int[] ids, (string Table, string StageColumn, string ParameterColumn) link, CancellationToken ct)
    {
        var sql = FamilySql + $"""
        SELECT {BaseParameterColumns} FROM public."BaseParameter" WHERE "baseRecipeId" = @family OR "id" = ANY(@ids) ORDER BY "id";
        SELECT l.{link.ParameterColumn}, b."id", b."name", b."baseRecipeId", r."id", r."name",
               CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" q WHERE q."baseRecipeId" = r."id") THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END
        FROM {link.Table} l JOIN public."BaseRecipeStage" b ON b."id" = l.{link.StageColumn}
        LEFT JOIN public."RecipeStage" s ON s."baseRecipeStageId" = b."id"
        LEFT JOIN public."Recipe" r ON r."id" = s."recipeId"
        WHERE l.{link.ParameterColumn} = ANY(@ids) ORDER BY l.{link.ParameterColumn}, b."id", s."id";
        SELECT p."id", p."baseParameterId", p."value", p."active", b."baseRecipeId", r."id", r."name",
               CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" q WHERE q."baseRecipeId" = r."id") THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END, s."id", s."name"
        FROM public."RecipeParameter" p LEFT JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
        LEFT JOIN public."BaseRecipeStage" b ON b."id" = s."baseRecipeStageId"
        LEFT JOIN public."Recipe" r ON r."id" = s."recipeId"
        WHERE p."baseParameterId" = ANY(@ids) ORDER BY p."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("family", familyId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var state = new ParameterState([], [], [], []);

        while (await reader.ReadAsync(ct)) state.Families.Add(new(reader.GetInt32(0), ReadText(reader, 1)));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Parameters.Add(ReadBaseParameter(reader));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Stages.Add(new(reader.GetInt32(0), reader.GetInt32(1), ReadText(reader, 2), NullableInt(reader, 3), NullableInt(reader, 4), ReadText(reader, 5), NullableInt(reader, 6)));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Values.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetBoolean(3), NullableInt(reader, 4), NullableInt(reader, 5), ReadText(reader, 6), NullableInt(reader, 7), NullableInt(reader, 8), ReadText(reader, 9)));

        return state;
    }

    /// <summary>Сохраняет отсутствие семейства или связи, не подменяя его нулём.</summary>
    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>Читает определение в порядке столбцов BaseParameterColumns.</summary>
    private static RecipeBaseParameterDto ReadBaseParameter(NpgsqlDataReader reader) => new() { Id = reader.GetInt32(0), BaseRecipeId = NullableInt(reader, 1), Order = reader.GetInt32(2), Name = ReadText(reader, 3), Description = ReadText(reader, 4), Tag = ReadText(reader, 5), Kind = (RecipeParameterKind)reader.GetInt32(6), Format = (RecipeParameterFormat)reader.GetInt32(7) };

    /// <summary>Формирует сравнение свойств для окна подтверждения.</summary>
    private static List<string> DescribeParameterChanges(RecipeBaseParameterDto? old, RecipeBaseParameterDto value)
    {
        var fields = new List<string>();

        void Add(string name, object? before, object? after)
        {
            if (old is null || !Equals(before, after)) fields.Add($"{name}: {before ?? "—"} → {after ?? "—"}");
        }

        Add("Family ID", old?.BaseRecipeId, value.BaseRecipeId);
        Add("Order", old?.Order, value.Order);
        Add("Name", old?.Name, value.Name);
        Add("Units", old?.Description, value.Description);
        Add("Tag", old?.Tag, value.Tag);
        Add("Type", old?.Kind, value.Kind);
        Add("Format", old?.Format, value.Format);

        return fields;
    }
}