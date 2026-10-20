using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using TechMES.Contracts.Recipes;

namespace TechMES.Infrastructure.PostgreSql.Recipes;

public sealed partial class PostgreSqlRecipeStore
{
    private sealed record StageInstance(int Id, int RecipeId, int? FamilyId, string RecipeName, int Number, string Name, bool Auto, string Prefix);
    private sealed record StageValue(int Id, int StageId, int ParameterId, double Value, bool Active);
    private sealed record StageState(bool FamilyExists, List<RecipeBaseStageDto> Definitions, List<RecipeBaseParameterDto> Parameters, List<StageInstance> Instances, List<StageValue> Values);

    /// <summary>Читает шаблоны выбранного семейства вместе с сохранёнными позициями карточек.</summary>
    public async Task<List<RecipeBaseStageDto>> GetBaseStagesAsync(int familyId, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var link = await FindTemplateParameterLinkAsync(connection, null, ct);

        return await ReadStageDefinitionsAsync(connection, null, familyId, link, ct);
    }

    /// <summary>Формирует отчёт без изменения данных.</summary>
    public async Task<RecipeBaseStagePreviewDto> PreviewBaseStageAsync(RecipeBaseStageRequest request, CancellationToken ct = default)
        => (await ProcessBaseStageAsync(request, false, ct)).Preview;

    /// <summary>Повторно проверяет подтверждение и синхронизирует все экземпляры шаблона.</summary>
    public async Task<RecipeBaseStageSaveResult> SaveBaseStageAsync(RecipeBaseStageRequest request, CancellationToken ct = default)
    {
        var result = await ProcessBaseStageAsync(request, true, ct);

        return new()
        {
            Status = result.Status,
            StageId = result.Id,
            Error = result.Preview.Errors.Count == 0 ? null : string.Join("\n", result.Preview.Errors)
        };
    }

    /// <summary>Состав, метаданные и значения проверяются до записи и повторно после подтверждения.</summary>
    private async Task<(RecipeBaseStagePreviewDto Preview, RecipeChangeStatus Status, int Id)> ProcessBaseStageAsync(RecipeBaseStageRequest request, bool save, CancellationToken ct)
    {
        var preview = new RecipeBaseStagePreviewDto();

        if (request.FamilyId <= 0 || request.Value is null || request.Value.Id < 0 || request.Value.FamilyId != request.FamilyId || request.Value.Slots is null || request.Value.Slots.Count != RecipeBaseStageDto.SlotCount || request.Value.Overflow is null)
        {
            preview.Errors.Add("Invalid stage or family.");
            return (preview, RecipeChangeStatus.Invalid, 0);
        }

        var value = request.Value;
        var name = (value.Name ?? "").Trim();
        var code = (value.Code ?? "").Trim();
        var ids = value.Slots.Where(id => id.HasValue).Select(id => id!.Value).ToArray();

        preview.Name = name;

        if (code.Length == 0) preview.Errors.Add("Template code is required.");

        // Один параметр может присутствовать в стадии только один раз.
        if (name.Length == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length || value.Overflow.Count > 0)
            preview.Errors.Add($"Enter a stage name and place each parameter once within the {RecipeBaseStageDto.SlotCount} slots. Remove or place overflow items first.");

        if (preview.Errors.Count > 0) return (preview, RecipeChangeStatus.Invalid, 0);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(save ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.RepeatableRead, ct);
        var link = await FindTemplateParameterLinkAsync(connection, transaction, ct);

        if (save)
        {
            // Совместимо с текущим сохранением ячейки: сначала p, затем s и определения.
            var lockSql = $"""
            SELECT pg_advisory_xact_lock(1380270928, 1);
            LOCK TABLE public."RecipePage" IN SHARE MODE;
            LOCK TABLE public."BaseRecipeStage" IN SHARE ROW EXCLUSIVE MODE;
            SELECT p."id" FROM public."RecipeParameter" p JOIN public."RecipeStage" s ON s."id" = p."recipeStageId"
            WHERE s."baseRecipeStageId" = @id ORDER BY p."id" FOR UPDATE OF p;
            SELECT "id" FROM public."RecipeStage" WHERE "baseRecipeStageId" = @id ORDER BY "id" FOR UPDATE;
            SELECT "id" FROM public."BaseRecipeStage" WHERE "id" = @id FOR UPDATE;
            SELECT l.{link.StageColumn} FROM {link.Table} l WHERE l.{link.StageColumn} = @id FOR UPDATE;
            SELECT "baseRecipeStageId" FROM public."RecipeStageLayout" WHERE "baseRecipeStageId" = @id FOR UPDATE;
            SELECT "id" FROM public."Recipe" ORDER BY "id" FOR SHARE;
            SELECT "id" FROM public."BaseParameter" ORDER BY "id" FOR SHARE;
            """;

            await using var gate = new NpgsqlCommand(lockSql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
            gate.Parameters.AddWithValue("id", value.Id);

            await gate.ExecuteNonQueryAsync(ct);
        }

        var state = await ReadStageStateAsync(connection, transaction, request.FamilyId, value.Id, link, ct);
        var old = state.Definitions.FirstOrDefault(stage => stage.Id == value.Id);
        var conflict = value.Id == 0 ? request.Original is not null : old is null || !SameStageDefinition(request.Original, old);

        if (conflict) preview.Errors.Add("The stage changed or no longer exists. Press Refresh before editing again.");
        if (!state.FamilyExists) preview.Errors.Add("No base recipe is configured for this family. Create a base recipe first.");

        if (ids.Any(id => !state.Parameters.Any(parameter => parameter.Id == id && parameter.BaseRecipeId == request.FamilyId)))
            preview.Errors.Add("Every parameter must belong to the selected family.");

        if (ids.Any(id => state.Parameters.Any(parameter => parameter.Id == id && !Enum.IsDefined(parameter.Kind))))
            preview.Errors.Add("A selected parameter has an unsupported type.");

        if (state.Instances.Any(stage => stage.FamilyId != request.FamilyId))
            preview.Errors.Add("The template is used by a recipe outside this family. Correct that link before synchronization.");

        if (state.Values.GroupBy(item => (item.StageId, item.ParameterId)).Any(group => group.Count() > 1))
            preview.Errors.Add("A recipe stage contains duplicate values for one parameter. Resolve duplicates before synchronization.");

        int[] previous = old is null ? [] : old.Slots.Where(id => id.HasValue).Select(id => id!.Value).Concat(old.Overflow).ToArray();

        string Label(int id) => $"[{id}] {state.Parameters.FirstOrDefault(parameter => parameter.Id == id)?.Name ?? "Unknown parameter"}";

        if (old is null) preview.Changes.Add("Create a new stage in this family.");
        if (old is not null && old.Name != name) preview.Changes.Add($"Name: {old.Name} → {name}. Update all recipe instances.");
        if (old is not null && old.Code != code) preview.Changes.Add($"Template code: {old.Code} → {code}. PLC prefixes stay unchanged.");

        foreach (var id in ids.Except(previous)) preview.Changes.Add($"Add parameter {Label(id)}.");
        foreach (var id in previous.Except(ids)) preview.Changes.Add($"Remove parameter {Label(id)}.");
        foreach (var id in state.Values.Select(item => item.ParameterId).Except(ids).Except(previous)) preview.Changes.Add($"Remove stale recipe parameter {Label(id)}.");

        if (old is not null && !old.Slots.SequenceEqual(value.Slots)) preview.Changes.Add("Save the new card positions.");

        foreach (var group in state.Instances.GroupBy(stage => stage.RecipeId).OrderBy(group => group.Key))
        {
            var stages = group.ToList();
            var stageIds = stages.Select(stage => stage.Id).ToHashSet();
            var rows = state.Values.Where(item => stageIds.Contains(item.StageId)).ToList();

            preview.Recipes.Add(new()
            {
                RecipeId = group.Key,
                RecipeName = stages[0].RecipeName,
                Stages = stages.Count,
                Added = stages.Sum(stage => ids.Count(id => !rows.Any(item => item.StageId == stage.Id && item.ParameterId == id))),
                Removed = rows.Count(item => !ids.Contains(item.ParameterId)),
                Preserved = rows.Count(item => ids.Contains(item.ParameterId))
            });
        }

        var json = JsonSerializer.Serialize(new { request.FamilyId, Value = value, Name = name, State = state }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        preview.Token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        if (preview.Errors.Count > 0) return (preview, conflict ? RecipeChangeStatus.Conflict : RecipeChangeStatus.Invalid, 0);
        if (!save) return (preview, RecipeChangeStatus.Saved, value.Id);

        if (!string.Equals(request.PreviewToken, preview.Token, StringComparison.Ordinal))
        {
            preview.Errors.Add("Stage data or recipe values changed after the preview. Review the impact again.");
            return (preview, RecipeChangeStatus.Conflict, 0);
        }

        var stageId = value.Id;

        var stageSql = stageId == 0 ? """
        INSERT INTO public."BaseRecipeStage" ("name", "description", "baseRecipeId") VALUES (@name, @code, @family) RETURNING "id";
        """ : """
        UPDATE public."BaseRecipeStage" SET "name" = @name, "description" = @code WHERE "id" = @id AND "baseRecipeId" = @family RETURNING "id";
        """;

        await using (var command = new NpgsqlCommand(stageSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", stageId);
            command.Parameters.AddWithValue("family", request.FamilyId);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("code", code);

            stageId = (int)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Stage save returned no ID."));
        }

        // Состав синхронизируем по ID. Значения оставшихся параметров не пересоздаём.
        // Имя экземпляров меняем только при переименовании самого шаблона.
        var syncSql = $"""
        DELETE FROM {link.Table} WHERE {link.StageColumn} = @id AND NOT ({link.ParameterColumn} = ANY(@ids));
        INSERT INTO {link.Table} ({link.StageColumn}, {link.ParameterColumn})
        SELECT @id, p."id" FROM public."BaseParameter" p WHERE p."id" = ANY(@ids)
        AND NOT EXISTS (SELECT 1 FROM {link.Table} l WHERE l.{link.StageColumn} = @id AND l.{link.ParameterColumn} = p."id");
        UPDATE public."RecipeStage" SET "name" = @name WHERE "baseRecipeStageId" = @id AND @rename;
        DELETE FROM public."RecipeParameter" p USING public."RecipeStage" s
        WHERE p."recipeStageId" = s."id" AND s."baseRecipeStageId" = @id AND NOT (p."baseParameterId" = ANY(@ids));
        UPDATE public."RecipeParameter" p SET "name" = b."name", "description" = b."description"
        FROM public."RecipeStage" s, public."BaseParameter" b
        WHERE p."recipeStageId" = s."id" AND s."baseRecipeStageId" = @id AND b."id" = p."baseParameterId" AND b."id" = ANY(@ids);
        INSERT INTO public."RecipeParameter" ("name", "value", "description", "active", "recipeStageId", "baseParameterId")
        SELECT b."name", 0.0, b."description", true, s."id", b."id"
        FROM public."RecipeStage" s CROSS JOIN public."BaseParameter" b
        WHERE s."baseRecipeStageId" = @id AND b."id" = ANY(@ids)
        AND NOT EXISTS (SELECT 1 FROM public."RecipeParameter" p WHERE p."recipeStageId" = s."id" AND p."baseParameterId" = b."id");
        INSERT INTO public."RecipeStageLayout" ("baseRecipeStageId", "slots") VALUES (@id, @slots)
        ON CONFLICT ("baseRecipeStageId") DO UPDATE SET "slots" = EXCLUDED."slots";
        """;

        await using (var command = new NpgsqlCommand(syncSql, connection, transaction) { CommandTimeout = GetCommandTimeout() })
        {
            command.Parameters.AddWithValue("id", stageId);
            command.Parameters.AddWithValue("ids", ids);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("rename", old is not null && old.Name != name);
            command.Parameters.AddWithValue("slots", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value.Slots));

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return (preview, RecipeChangeStatus.Saved, stageId);
    }

    /// <summary>Читает раскладку, сохраняет старые связи и исключает повторное размещение параметров.</summary>
    private async Task<List<RecipeBaseStageDto>> ReadStageDefinitionsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int familyId, (string Table, string StageColumn, string ParameterColumn) link, CancellationToken ct)
    {
        var sql = $"""
        SELECT b."id", b."name", b."description", l."slots"::text
        FROM public."BaseRecipeStage" b LEFT JOIN public."RecipeStageLayout" l ON l."baseRecipeStageId" = b."id"
        WHERE b."baseRecipeId" = @family ORDER BY b."name", b."id";
        SELECT l.{link.StageColumn}, p."id" FROM {link.Table} l
        JOIN public."BaseRecipeStage" b ON b."id" = l.{link.StageColumn}
        JOIN public."BaseParameter" p ON p."id" = l.{link.ParameterColumn}
        WHERE b."baseRecipeId" = @family ORDER BY b."id", p."type", p."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("family", familyId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var stages = new List<RecipeBaseStageDto>();

        while (await reader.ReadAsync(ct))
            stages.Add(new() { Id = reader.GetInt32(0), FamilyId = familyId, Name = ReadText(reader, 1), Code = ReadText(reader, 2), Slots = reader.IsDBNull(3) ? Enumerable.Repeat<int?>(null, RecipeBaseStageDto.SlotCount).ToList() : JsonSerializer.Deserialize<List<int?>>(reader.GetString(3)) ?? throw new InvalidOperationException("Invalid stage layout.") });

        await reader.NextResultAsync(ct);

        var members = new Dictionary<int, List<int>>();

        while (await reader.ReadAsync(ct))
        {
            if (!members.TryGetValue(reader.GetInt32(0), out var list)) members[reader.GetInt32(0)] = list = [];
            list.Add(reader.GetInt32(1));
        }

        foreach (var stage in stages)
            NormalizeStageSlots(stage, members.GetValueOrDefault(stage.Id) ?? []);

        return stages;
    }

    /// <summary>Возвращает 32 места, сохраняя связи и вынося лишние параметры в Overflow без удаления из БД.</summary>
    private static void NormalizeStageSlots(RecipeBaseStageDto stage, List<int> members)
    {
        var stored = stage.Slots;

        if (stored.Count != RecipeBaseStageDto.SlotCount && stored.Count != 40)
            throw new InvalidOperationException("Stage layout must contain 32 or 40 slots.");

        var allowed = members.ToHashSet();
        var used = new HashSet<int>();

        stage.Slots = Enumerable.Repeat<int?>(null, RecipeBaseStageDto.SlotCount).ToList();
        stage.Overflow = [];

        for (var i = 0; i < RecipeBaseStageDto.SlotCount; i++)
            if (stored[i] is int id && allowed.Contains(id) && used.Add(id)) stage.Slots[i] = id;

        var remaining = stored.Skip(RecipeBaseStageDto.SlotCount).OfType<int>().Concat(members);

        foreach (var id in remaining)
        {
            if (!allowed.Contains(id) || !used.Add(id)) continue;

            var slot = stage.Slots.FindIndex(item => item is null);

            if (slot >= 0) stage.Slots[slot] = id;
            else stage.Overflow.Add(id);
        }
    }

    /// <summary>Снимок включает реальные значения всех экземпляров, а не только состав шаблона.</summary>
    private async Task<StageState> ReadStageStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int familyId, int stageId, (string Table, string StageColumn, string ParameterColumn) link, CancellationToken ct)
    {
        var definitions = await ReadStageDefinitionsAsync(connection, transaction, familyId, link, ct);

        var sql = $"""
        SELECT EXISTS (SELECT 1 FROM public."RecipePage" p JOIN public."Recipe" r ON r."id" = p."baseRecipeId" WHERE r."id" = @family);
        SELECT {BaseParameterColumns} FROM public."BaseParameter" WHERE "baseRecipeId" = @family
        OR "id" IN (SELECT l.{link.ParameterColumn} FROM {link.Table} l WHERE l.{link.StageColumn} = @id)
        OR "id" IN (SELECT p."baseParameterId" FROM public."RecipeParameter" p JOIN public."RecipeStage" s ON s."id" = p."recipeStageId" WHERE s."baseRecipeStageId" = @id) ORDER BY "id";
        SELECT s."id", s."recipeId", CASE WHEN EXISTS (SELECT 1 FROM public."RecipePage" q WHERE q."baseRecipeId" = r."id") THEN r."id" ELSE COALESCE(r."baseRecipeId", r."id") END,
               r."name", s."stageNumb", s."name", s."auto", s."description"
        FROM public."RecipeStage" s LEFT JOIN public."Recipe" r ON r."id" = s."recipeId" WHERE s."baseRecipeStageId" = @id ORDER BY s."id";
        SELECT p."id", p."recipeStageId", p."baseParameterId", p."value", p."active"
        FROM public."RecipeParameter" p JOIN public."RecipeStage" s ON s."id" = p."recipeStageId" WHERE s."baseRecipeStageId" = @id ORDER BY p."id";
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = GetCommandTimeout() };
        command.Parameters.AddWithValue("family", familyId);
        command.Parameters.AddWithValue("id", stageId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var state = new StageState(reader.GetBoolean(0), definitions, [], [], []);

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Parameters.Add(ReadBaseParameter(reader));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Instances.Add(new(reader.GetInt32(0), NullableInt(reader, 1) ?? 0, NullableInt(reader, 2), ReadText(reader, 3), reader.GetInt32(4), ReadText(reader, 5), reader.GetBoolean(6), ReadText(reader, 7)));

        await reader.NextResultAsync(ct);

        while (await reader.ReadAsync(ct)) state.Values.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3), reader.GetBoolean(4)));

        return state;
    }

    /// <summary>Проверяет исходное определение и позиции, а не ссылочное равенство DTO.</summary>
    private static bool SameStageDefinition(RecipeBaseStageDto? a, RecipeBaseStageDto b)
        => a is not null && a.Id == b.Id && a.FamilyId == b.FamilyId && a.Name == b.Name && a.Code == b.Code && a.Slots is not null && a.Overflow is not null && a.Slots.SequenceEqual(b.Slots) && a.Overflow.SequenceEqual(b.Overflow);
}