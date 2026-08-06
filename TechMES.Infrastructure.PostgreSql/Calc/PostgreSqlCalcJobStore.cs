using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using TechMES.Application.Calc;
using TechMES.Contracts.Calc;

namespace TechMES.Infrastructure.PostgreSql.Calc;

/// <summary>
/// PostgreSQL-хранилище расчётных заданий.
///
/// Задание, его входы и выходы сохраняются одной транзакцией.
/// Обновление защищено полем Revision от одновременного редактирования
/// через Maintenance и WEB.
/// </summary>
public sealed class PostgreSqlCalcJobStore : ICalcJobStore
{
    private readonly string _connectionString;

    public PostgreSqlCalcJobStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");
    }

    /// <summary>
    /// Возвращает все задания вместе с входами и выходами.
    /// Для всего списка выполняются три SQL-запроса, а не отдельные
    /// запросы входов и выходов для каждого задания.
    /// </summary>
    public async Task<IReadOnlyList<CalcJobDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await ReadJobsAsync(connection, null, ct);
    }

    /// <summary>
    /// Возвращает одно задание или null.
    /// </summary>
    public async Task<CalcJobDto?> GetAsync(long id, CancellationToken ct = default)
    {
        if (id <= 0)
            return null;

        await using var connection = await OpenConnectionAsync(ct);
        var jobs = await ReadJobsAsync(connection, id, ct);
        return jobs.SingleOrDefault();
    }

    /// <summary>
    /// Создаёт задание, его входы, выходы и начальное состояние NeverRun.
    /// </summary>
    public async Task<CalcJobDto> CreateAsync(CalcJobSaveRequest request, string? changedBy, CancellationToken ct = default)
    {
        var normalized = NormalizeRequest(request, requireRevision: false);
        var userName = NormalizeOptionalText(changedBy);

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        long jobId;

        try
        {
            const string sql = """
            INSERT INTO public.calc_job
                (equipment_name, name, description, definition_code, definition_version,
                 enabled, period_ms, write_enabled, sort_order, revision,
                 created_by, updated_by, created_at, updated_at)
            VALUES
                (@equipment_name, @name, @description, @definition_code, @definition_version,
                 @enabled, @period_ms, @write_enabled, @sort_order, 1,
                 @changed_by, @changed_by, now(), now())
            RETURNING id;
            """;

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddNullable(command, "equipment_name", NpgsqlDbType.Text, normalized.EquipmentName);
            command.Parameters.AddWithValue("name", normalized.Name);
            AddNullable(command, "description", NpgsqlDbType.Text, normalized.Description);
            command.Parameters.AddWithValue("definition_code", normalized.DefinitionCode);
            command.Parameters.AddWithValue("definition_version", normalized.DefinitionVersion);
            command.Parameters.AddWithValue("enabled", normalized.Enabled);
            command.Parameters.AddWithValue("period_ms", normalized.PeriodMs);
            command.Parameters.AddWithValue("write_enabled", normalized.WriteEnabled);
            command.Parameters.AddWithValue("sort_order", normalized.SortOrder);
            AddNullable(command, "changed_by", NpgsqlDbType.Text, userName);

            jobId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

            await InsertInputsAsync(connection, transaction, jobId, normalized.Inputs, ct);
            await InsertOutputsAsync(connection, transaction, jobId, normalized.Outputs, ct);
            await ResetStateAsync(connection, transaction, jobId, normalized.DefinitionVersion, 1, null, null, ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        return await GetAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Created calculation job {jobId} could not be read.");
    }

    /// <summary>
    /// Обновляет задание только при совпадении ExpectedRevision.
    ///
    /// Входы и выходы заменяются полным снимком внутри той же транзакции.
    /// После изменения старый результат становится недействительным,
    /// поэтому состояние возвращается в NeverRun.
    /// </summary>
    public async Task<CalcJobDto?> UpdateAsync(long id, CalcJobSaveRequest request, string? changedBy, CancellationToken ct = default)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Calculation job id must be greater than zero.");

        var normalized = NormalizeRequest(request, requireRevision: true);
        var expectedRevision = normalized.ExpectedRevision!.Value;
        var userName = NormalizeOptionalText(changedBy);

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        long newRevision;

        try
        {
            const string sql = """
            UPDATE public.calc_job
            SET equipment_name = @equipment_name,
                name = @name,
                description = @description,
                definition_code = @definition_code,
                definition_version = @definition_version,
                enabled = @enabled,
                period_ms = @period_ms,
                write_enabled = @write_enabled,
                sort_order = @sort_order,
                revision = revision + 1,
                updated_by = @changed_by,
                updated_at = now()
            WHERE id = @id
              AND revision = @expected_revision
            RETURNING revision;
            """;

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            AddNullable(command, "equipment_name", NpgsqlDbType.Text, normalized.EquipmentName);
            command.Parameters.AddWithValue("name", normalized.Name);
            AddNullable(command, "description", NpgsqlDbType.Text, normalized.Description);
            command.Parameters.AddWithValue("definition_code", normalized.DefinitionCode);
            command.Parameters.AddWithValue("definition_version", normalized.DefinitionVersion);
            command.Parameters.AddWithValue("enabled", normalized.Enabled);
            command.Parameters.AddWithValue("period_ms", normalized.PeriodMs);
            command.Parameters.AddWithValue("write_enabled", normalized.WriteEnabled);
            command.Parameters.AddWithValue("sort_order", normalized.SortOrder);
            AddNullable(command, "changed_by", NpgsqlDbType.Text, userName);

            var revisionResult = await command.ExecuteScalarAsync(ct);

            if (revisionResult is null)
            {
                var currentRevision = await ReadCurrentRevisionAsync(connection, transaction, id, ct);

                await transaction.RollbackAsync(ct);

                if (!currentRevision.HasValue)
                    return null;

                throw new CalcJobRevisionConflictException(id, expectedRevision, currentRevision.Value);
            }

            newRevision = Convert.ToInt64(revisionResult);

            await DeleteBindingsAsync(connection, transaction, id, ct);
            await InsertInputsAsync(connection, transaction, id, normalized.Inputs, ct);
            await InsertOutputsAsync(connection, transaction, id, normalized.Outputs, ct);

            await ResetStateAsync(
                connection,
                transaction,
                id,
                normalized.DefinitionVersion,
                newRevision,
                "configuration.changed",
                "Calculation configuration was changed and must be executed again.",
                ct);

            await transaction.CommitAsync(ct);
        }
        catch (CalcJobRevisionConflictException)
        {
            throw;
        }
        catch
        {
            await SafeRollbackAsync(transaction, ct);
            throw;
        }

        return await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Updated calculation job {id} could not be read.");
    }

    /// <summary>
    /// Удаляет задание. Дочерние входы, выходы и состояние
    /// удаляются каскадно средствами PostgreSQL.
    /// </summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        if (id <= 0)
            return false;

        await using var connection = await OpenConnectionAsync(ct);

        const string sql = """
        DELETE FROM public.calc_job
        WHERE id = @id
        RETURNING id;
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        try
        {
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new InvalidOperationException(
                $"Calculation job {id} cannot be deleted because another calculation depends on it.",
                ex);
        }
    }

    /// <summary>
    /// Загружает основные записи заданий и дочерние коллекции.
    /// </summary>
    private static async Task<IReadOnlyList<CalcJobDto>> ReadJobsAsync(NpgsqlConnection connection, long? jobId, CancellationToken ct)
    {
        const string sql = """
        SELECT id, equipment_name, name, description, definition_code, definition_version,
               enabled, period_ms, write_enabled, sort_order, revision, created_at, updated_at
        FROM public.calc_job
        WHERE @job_id IS NULL OR id = @job_id
        ORDER BY sort_order, name, id;
        """;

        var jobs = new List<CalcJobDto>();

        await using (var command = new NpgsqlCommand(sql, connection))
        {
            AddNullable(command, "job_id", NpgsqlDbType.Bigint, jobId);

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                jobs.Add(new CalcJobDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    EquipmentName = ReadNullableString(reader, "equipment_name"),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    Description = ReadNullableString(reader, "description"),
                    DefinitionCode = reader.GetString(reader.GetOrdinal("definition_code")),
                    DefinitionVersion = reader.GetString(reader.GetOrdinal("definition_version")),
                    Enabled = reader.GetBoolean(reader.GetOrdinal("enabled")),
                    PeriodMs = reader.GetInt32(reader.GetOrdinal("period_ms")),
                    WriteEnabled = reader.GetBoolean(reader.GetOrdinal("write_enabled")),
                    SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
                    Revision = reader.GetInt64(reader.GetOrdinal("revision")),
                    CreatedAtUtc = ReadUtcDateTimeOffset(reader, "created_at"),
                    UpdatedAtUtc = ReadUtcDateTimeOffset(reader, "updated_at")
                });
            }
        }

        if (jobs.Count == 0)
            return jobs;

        var jobsById = jobs.ToDictionary(job => job.Id);
        var jobIds = jobs.Select(job => job.Id).ToArray();

        await ReadInputsAsync(connection, jobsById, jobIds, ct);
        await ReadOutputsAsync(connection, jobsById, jobIds, ct);

        return jobs;
    }

    /// <summary>
    /// Загружает входы сразу для всех выбранных заданий.
    /// </summary>
    private static async Task ReadInputsAsync(NpgsqlConnection connection, IReadOnlyDictionary<long, CalcJobDto> jobsById, long[] jobIds, CancellationToken ct)
    {
        const string sql = """
        SELECT id, job_id, parameter_key, source_type, tag_name,
               constant_value::text AS constant_value,
               source_job_id, source_output_key, max_age_seconds, sort_order
        FROM public.calc_job_input
        WHERE job_id = ANY(@job_ids)
        ORDER BY job_id, sort_order, id;
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, jobIds);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var jobId = reader.GetInt64(reader.GetOrdinal("job_id"));

            if (!jobsById.TryGetValue(jobId, out var job))
                continue;

            job.Inputs.Add(new CalcJobInputDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ParameterKey = reader.GetString(reader.GetOrdinal("parameter_key")),
                SourceType = ParseSourceType(reader.GetString(reader.GetOrdinal("source_type"))),
                TagName = ReadNullableString(reader, "tag_name"),
                ConstantValue = ReadNullableJson(reader, "constant_value"),
                SourceJobId = ReadNullableInt64(reader, "source_job_id"),
                SourceOutputKey = ReadNullableString(reader, "source_output_key"),
                MaxAgeSeconds = ReadNullableInt32(reader, "max_age_seconds"),
                SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order"))
            });
        }
    }

    /// <summary>
    /// Загружает выходы сразу для всех выбранных заданий.
    /// </summary>
    private static async Task ReadOutputsAsync(NpgsqlConnection connection, IReadOnlyDictionary<long, CalcJobDto> jobsById, long[] jobIds, CancellationToken ct)
    {
        const string sql = """
        SELECT id, job_id, output_key, tag_name, write_enabled, scale, offset_value, sort_order
        FROM public.calc_job_output
        WHERE job_id = ANY(@job_ids)
        ORDER BY job_id, sort_order, id;
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, jobIds);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var jobId = reader.GetInt64(reader.GetOrdinal("job_id"));

            if (!jobsById.TryGetValue(jobId, out var job))
                continue;

            job.Outputs.Add(new CalcJobOutputDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                OutputKey = reader.GetString(reader.GetOrdinal("output_key")),
                TagName = ReadNullableString(reader, "tag_name"),
                WriteEnabled = reader.GetBoolean(reader.GetOrdinal("write_enabled")),
                Scale = reader.GetDouble(reader.GetOrdinal("scale")),
                Offset = reader.GetDouble(reader.GetOrdinal("offset_value")),
                SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order"))
            });
        }
    }

    /// <summary>
    /// Вставляет полный набор входных привязок.
    /// </summary>
    private static async Task InsertInputsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, IReadOnlyList<CalcJobInputSaveDto> inputs, CancellationToken ct)
    {
        const string sql = """
        INSERT INTO public.calc_job_input
            (job_id, parameter_key, source_type, tag_name, constant_value,
             source_job_id, source_output_key, max_age_seconds, sort_order)
        VALUES
            (@job_id, @parameter_key, @source_type, @tag_name, @constant_value,
             @source_job_id, @source_output_key, @max_age_seconds, @sort_order);
        """;

        foreach (var input in inputs)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("parameter_key", input.ParameterKey);
            command.Parameters.AddWithValue("source_type", input.SourceType.ToString());
            AddNullable(command, "tag_name", NpgsqlDbType.Text, input.TagName);
            AddJson(command, "constant_value", input.ConstantValue);
            AddNullable(command, "source_job_id", NpgsqlDbType.Bigint, input.SourceJobId);
            AddNullable(command, "source_output_key", NpgsqlDbType.Text, input.SourceOutputKey);
            AddNullable(command, "max_age_seconds", NpgsqlDbType.Integer, input.MaxAgeSeconds);
            command.Parameters.AddWithValue("sort_order", input.SortOrder);

            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Вставляет полный набор выходных привязок.
    /// </summary>
    private static async Task InsertOutputsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, IReadOnlyList<CalcJobOutputSaveDto> outputs, CancellationToken ct)
    {
        const string sql = """
        INSERT INTO public.calc_job_output
            (job_id, output_key, tag_name, write_enabled, scale, offset_value, sort_order)
        VALUES
            (@job_id, @output_key, @tag_name, @write_enabled, @scale, @offset_value, @sort_order);
        """;

        foreach (var output in outputs)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("output_key", output.OutputKey);
            AddNullable(command, "tag_name", NpgsqlDbType.Text, output.TagName);
            command.Parameters.AddWithValue("write_enabled", output.WriteEnabled);
            command.Parameters.AddWithValue("scale", output.Scale);
            command.Parameters.AddWithValue("offset_value", output.Offset);
            command.Parameters.AddWithValue("sort_order", output.SortOrder);

            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Удаляет старый снимок входов и выходов перед обновлением.
    /// </summary>
    private static async Task DeleteBindingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, CancellationToken ct)
    {
        const string sql = """
        DELETE FROM public.calc_job_input WHERE job_id = @job_id;
        DELETE FROM public.calc_job_output WHERE job_id = @job_id;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("job_id", jobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Создаёт либо сбрасывает текущее диагностическое состояние задания.
    /// </summary>
    private static async Task ResetStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, string definitionVersion, long revision, string? reasonCode, string? reasonMessage, CancellationToken ct)
    {
        const string sql = """
        INSERT INTO public.calc_job_state
            (job_id, status, reason_code, reason_message, definition_version,
             configuration_revision, cycle_number, updated_at)
        VALUES
            (@job_id, 'NeverRun', @reason_code, @reason_message, @definition_version,
             @revision, 0, now())
        ON CONFLICT (job_id) DO UPDATE SET
            status = 'NeverRun',
            reason_code = EXCLUDED.reason_code,
            reason_message = EXCLUDED.reason_message,
            definition_version = EXCLUDED.definition_version,
            configuration_revision = EXCLUDED.configuration_revision,
            cycle_number = 0,
            last_started_at = NULL,
            last_completed_at = NULL,
            last_success_at = NULL,
            last_duration_ms = NULL,
            last_inputs = NULL,
            last_outputs = NULL,
            updated_at = now();
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("definition_version", definitionVersion);
        command.Parameters.AddWithValue("revision", revision);
        AddNullable(command, "reason_code", NpgsqlDbType.Text, reasonCode);
        AddNullable(command, "reason_message", NpgsqlDbType.Text, reasonMessage);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Читает текущую Revision для различения NotFound и конфликта.
    /// </summary>
    private static async Task<long?> ReadCurrentRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, CancellationToken ct)
    {
        const string sql = "SELECT revision FROM public.calc_job WHERE id = @id;";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", jobId);

        var result = await command.ExecuteScalarAsync(ct);
        return result is null ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// Нормализует запрос и проверяет ограничения,
    /// которые должны быть понятны до выполнения SQL.
    /// </summary>
    private static CalcJobSaveRequest NormalizeRequest(CalcJobSaveRequest request, bool requireRevision)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (requireRevision && (!request.ExpectedRevision.HasValue || request.ExpectedRevision.Value <= 0))
            throw new ArgumentException("ExpectedRevision must be greater than zero when updating a calculation job.", nameof(request));

        if (!requireRevision && request.ExpectedRevision.HasValue)
            throw new ArgumentException("ExpectedRevision must be null when creating a calculation job.", nameof(request));

        if (request.PeriodMs <= 0)
            throw new ArgumentException("Calculation period must be greater than zero.", nameof(request));

        var inputs = NormalizeInputs(request.Inputs ?? []);
        var outputs = NormalizeOutputs(request.Outputs ?? []);

        return new CalcJobSaveRequest
        {
            EquipmentName = NormalizeOptionalText(request.EquipmentName),
            Name = NormalizeRequiredText(request.Name, "Calculation job name"),
            Description = NormalizeOptionalText(request.Description),
            DefinitionCode = NormalizeRequiredText(request.DefinitionCode, "Calculation definition code"),
            DefinitionVersion = NormalizeRequiredText(request.DefinitionVersion, "Calculation definition version"),
            Enabled = request.Enabled,
            PeriodMs = request.PeriodMs,
            WriteEnabled = request.WriteEnabled,
            SortOrder = request.SortOrder,
            ExpectedRevision = request.ExpectedRevision,
            Inputs = inputs,
            Outputs = outputs
        };
    }

    /// <summary>
    /// Нормализует и проверяет входные привязки.
    /// </summary>
    private static List<CalcJobInputSaveDto> NormalizeInputs(IEnumerable<CalcJobInputSaveDto> source)
    {
        var result = new List<CalcJobInputSaveDto>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in source)
        {
            ArgumentNullException.ThrowIfNull(input);

            var parameterKey = NormalizeRequiredText(input.ParameterKey, "Calculation input parameter key");

            if (!keys.Add(parameterKey))
                throw new ArgumentException($"Calculation input '{parameterKey}' is specified more than once.");

            if (!Enum.IsDefined(input.SourceType))
                throw new ArgumentException($"Calculation input '{parameterKey}' contains an unsupported source type.");

            if (input.MaxAgeSeconds.HasValue && input.MaxAgeSeconds.Value <= 0)
                throw new ArgumentException($"Calculation input '{parameterKey}' MaxAgeSeconds must be greater than zero.");

            var tagName = NormalizeOptionalText(input.TagName);
            var sourceOutputKey = NormalizeOptionalText(input.SourceOutputKey);
            var constantValue = NormalizeConstant(input.ConstantValue, parameterKey);

            switch (input.SourceType)
            {
                case CalcInputSourceTypeDto.Tag:
                    if (tagName is null || constantValue.HasValue || input.SourceJobId.HasValue || sourceOutputKey is not null)
                        throw new ArgumentException($"Tag input '{parameterKey}' must contain only TagName.");
                    break;

                case CalcInputSourceTypeDto.Constant:
                    if (!constantValue.HasValue || tagName is not null || input.SourceJobId.HasValue || sourceOutputKey is not null)
                        throw new ArgumentException($"Constant input '{parameterKey}' must contain only ConstantValue.");
                    break;

                case CalcInputSourceTypeDto.CalculationOutput:
                    if (!input.SourceJobId.HasValue || input.SourceJobId.Value <= 0 || sourceOutputKey is null
                        || tagName is not null || constantValue.HasValue)
                    {
                        throw new ArgumentException(
                            $"CalculationOutput input '{parameterKey}' must contain SourceJobId and SourceOutputKey only.");
                    }
                    break;
            }

            result.Add(new CalcJobInputSaveDto
            {
                ParameterKey = parameterKey,
                SourceType = input.SourceType,
                TagName = tagName,
                ConstantValue = constantValue,
                SourceJobId = input.SourceJobId,
                SourceOutputKey = sourceOutputKey,
                MaxAgeSeconds = input.MaxAgeSeconds,
                SortOrder = input.SortOrder
            });
        }

        return result;
    }

    /// <summary>
    /// Нормализует и проверяет выходные привязки.
    /// </summary>
    private static List<CalcJobOutputSaveDto> NormalizeOutputs(IEnumerable<CalcJobOutputSaveDto> source)
    {
        var result = new List<CalcJobOutputSaveDto>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in source)
        {
            ArgumentNullException.ThrowIfNull(output);

            var outputKey = NormalizeRequiredText(output.OutputKey, "Calculation output key");
            var tagName = NormalizeOptionalText(output.TagName);

            if (!keys.Add(outputKey))
                throw new ArgumentException($"Calculation output '{outputKey}' is specified more than once.");

            if (!double.IsFinite(output.Scale) || !double.IsFinite(output.Offset))
                throw new ArgumentException($"Calculation output '{outputKey}' Scale and Offset must be finite numbers.");

            if (output.WriteEnabled && tagName is null)
                throw new ArgumentException($"Calculation output '{outputKey}' requires TagName when writing is enabled.");

            result.Add(new CalcJobOutputSaveDto
            {
                OutputKey = outputKey,
                TagName = tagName,
                WriteEnabled = output.WriteEnabled,
                Scale = output.Scale,
                Offset = output.Offset,
                SortOrder = output.SortOrder
            });
        }

        return result;
    }

    /// <summary>
    /// Разрешает только простые JSON-значения констант.
    /// </summary>
    private static JsonElement? NormalizeConstant(JsonElement? value, string parameterKey)
    {
        if (!value.HasValue)
            return null;

        var element = value.Value;

        if (element.ValueKind is not JsonValueKind.Number
            and not JsonValueKind.String
            and not JsonValueKind.True
            and not JsonValueKind.False)
        {
            throw new ArgumentException(
                $"Constant input '{parameterKey}' must be a number, string or boolean.");
        }

        return element.Clone();
    }

    private static CalcInputSourceTypeDto ParseSourceType(string value)
    {
        if (Enum.TryParse<CalcInputSourceTypeDto>(value, true, out var result) && Enum.IsDefined(result))
            return result;

        throw new InvalidOperationException($"Unsupported calculation input source type '{value}' in PostgreSQL.");
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task SafeRollbackAsync(NpgsqlTransaction transaction, CancellationToken ct)
    {
        try
        {
            await transaction.RollbackAsync(ct);
        }
        catch
        {
            // Сохраняем исходную ошибку операции, а не ошибку повторного rollback.
        }
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static void AddJson(NpgsqlCommand command, string name, JsonElement? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = value.HasValue ? value.Value.GetRawText() : DBNull.Value;
    }

    private static string NormalizeRequiredText(string? value, string fieldName)
    {
        var normalized = NormalizeOptionalText(value);

        return normalized
            ?? throw new ArgumentException($"{fieldName} is required.");
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadNullableInt32(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? ReadNullableInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static JsonElement? ReadNullableJson(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
            return null;

        using var document = JsonDocument.Parse(reader.GetString(ordinal));
        return document.RootElement.Clone();
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(NpgsqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));

        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(value.ToUniversalTime());
    }
}