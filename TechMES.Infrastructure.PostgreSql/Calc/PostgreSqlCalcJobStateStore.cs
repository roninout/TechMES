using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using TechMES.Application.Calc;
using TechMES.Contracts.Calc;

namespace TechMES.Infrastructure.PostgreSql.Calc;

/// <summary>
/// PostgreSQL-хранилище текущего состояния расчётных заданий.
///
/// Calc.Service не подключается к этому классу напрямую.
/// Все изменения поступают через Runtime.Service.
/// </summary>
public sealed class PostgreSqlCalcJobStateStore : ICalcJobStateStore
{
    private readonly string _connectionString;

    public PostgreSqlCalcJobStateStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");
    }

    /// <summary>
    /// Возвращает состояния всех заданий.
    /// </summary>
    public async Task<IReadOnlyList<CalcJobStateDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await ReadStatesAsync(connection, null, ct);
    }

    /// <summary>
    /// Возвращает состояние одного задания.
    /// </summary>
    public async Task<CalcJobStateDto?> GetAsync(long jobId, CancellationToken ct = default)
    {
        if (jobId <= 0)
            return null;

        await using var connection = await OpenConnectionAsync(ct);
        var states = await ReadStatesAsync(connection, jobId, ct);
        return states.SingleOrDefault();
    }

    /// <summary>
    /// Сохраняет результаты одной транзакцией.
    ///
    /// Логические конфликты Revision или disabled-job возвращаются
    /// как rejected items и не откатывают корректные результаты.
    /// </summary>
    public async Task<CalcExecutionResultBatchResponse> SaveResultsAsync(CalcExecutionResultBatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var response = new CalcExecutionResultBatchResponse
        {
            RequestedCount = request.Items.Count
        };

        try
        {
            foreach (var item in request.Items)
            {
                var itemResponse = await SaveResultAsync(connection, transaction, item, ct);
                response.Items.Add(itemResponse);

                if (itemResponse.Accepted)
                    response.AcceptedCount++;
                else
                    response.RejectedCount++;
            }

            await transaction.CommitAsync(ct);
            return response;
        }
        catch
        {
            await SafeRollbackAsync(transaction, ct);
            throw;
        }
    }

    /// <summary>
    /// Обновляет одно состояние при совпадении текущей конфигурации.
    ///
    /// Persisted CycleNumber и диагностические счётчики увеличиваются
    /// непосредственно в PostgreSQL и не зависят от перезапуска Calc.Service.
    /// </summary>
    private static async Task<CalcExecutionResultSaveResponse> SaveResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CalcExecutionResultItemDto item, CancellationToken ct)
    {
        const string sql = """
            UPDATE public.calc_job_state AS state
            SET status = @status,
                reason_code = @reason_code,
                reason_message = @reason_message,
                definition_version = @definition_version,
                configuration_revision = @configuration_revision,

                cycle_number = state.cycle_number + 1,

                success_count = state.success_count
                    + CASE WHEN @status = 'Success' THEN 1 ELSE 0 END,

                skipped_count = state.skipped_count
                    + CASE WHEN @status = 'Skipped' THEN 1 ELSE 0 END,

                error_count = state.error_count
                    + CASE WHEN @status = 'Error' THEN 1 ELSE 0 END,

                consecutive_skipped_count = CASE
                    WHEN @status = 'Skipped' THEN state.consecutive_skipped_count + 1
                    ELSE 0
                END,

                consecutive_error_count = CASE
                    WHEN @status = 'Error' THEN state.consecutive_error_count + 1
                    ELSE 0
                END,

                last_started_at = @started_at,
                last_completed_at = @completed_at,

                last_success_at = CASE
                    WHEN @status = 'Success' THEN @completed_at
                    ELSE state.last_success_at
                END,

                last_duration_ms = @duration_ms,
                last_inputs = @last_inputs,
                last_outputs = @last_outputs,
                updated_at = now()

            FROM public.calc_job AS job
            WHERE state.job_id = @job_id
              AND job.id = state.job_id
              AND job.enabled = true
              AND job.revision = @configuration_revision
              AND job.definition_code = @definition_code
              AND job.definition_version = @definition_version
              AND (
                  state.last_completed_at IS NULL
                  OR state.last_completed_at < @completed_at
              )

            RETURNING state.cycle_number;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("job_id", item.JobId);
        command.Parameters.AddWithValue("status", item.Status.ToString());
        command.Parameters.AddWithValue("configuration_revision", item.ConfigurationRevision);
        command.Parameters.AddWithValue("definition_code", item.DefinitionCode);
        command.Parameters.AddWithValue("definition_version", item.DefinitionVersion);
        command.Parameters.AddWithValue("started_at", item.StartedAtUtc);
        command.Parameters.AddWithValue("completed_at", item.CompletedAtUtc);
        command.Parameters.AddWithValue("duration_ms", item.DurationMs);

        AddNullable(command, "reason_code", NpgsqlDbType.Text, NormalizeOptionalText(item.ReasonCode));
        AddNullable(command, "reason_message", NpgsqlDbType.Text, NormalizeOptionalText(item.ReasonMessage));
        AddJson(command, "last_inputs", item.Inputs);
        AddJson(command, "last_outputs", item.Outputs);

        var cycleResult = await command.ExecuteScalarAsync(ct);

        if (cycleResult is not null)
        {
            return new CalcExecutionResultSaveResponse
            {
                JobId = item.JobId,
                ServiceCycleNumber = item.ServiceCycleNumber,
                Accepted = true,
                PersistedCycleNumber = Convert.ToInt64(cycleResult)
            };
        }

        return await BuildRejectionAsync(connection, transaction, item, ct);
    }

    /// <summary>
    /// Определяет точную причину отклонения результата.
    /// </summary>
    private static async Task<CalcExecutionResultSaveResponse> BuildRejectionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CalcExecutionResultItemDto item, CancellationToken ct)
    {
        const string sql = """
        SELECT job.enabled,
               job.revision,
               job.definition_code,
               job.definition_version,
               state.job_id IS NOT NULL AS state_exists,
               state.last_completed_at
        FROM public.calc_job AS job
        LEFT JOIN public.calc_job_state AS state ON state.job_id = job.id
        WHERE job.id = @job_id;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("job_id", item.JobId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return Rejected(item, "job.not-found", $"Calculation job {item.JobId} was not found.");

        if (!reader.GetBoolean(reader.GetOrdinal("enabled")))
            return Rejected(item, "job.disabled", $"Calculation job {item.JobId} is disabled.");

        var revision = reader.GetInt64(reader.GetOrdinal("revision"));

        if (revision != item.ConfigurationRevision)
        {
            return Rejected(
                item,
                "job.revision-mismatch",
                $"Calculation job {item.JobId} revision changed from {item.ConfigurationRevision} to {revision}.");
        }

        var definitionCode = reader.GetString(reader.GetOrdinal("definition_code"));
        var definitionVersion = reader.GetString(reader.GetOrdinal("definition_version"));

        if (!string.Equals(definitionCode, item.DefinitionCode, StringComparison.Ordinal)
            || !string.Equals(definitionVersion, item.DefinitionVersion, StringComparison.Ordinal))
        {
            return Rejected(
                item,
                "definition.mismatch",
                $"Calculation job {item.JobId} definition or version was changed.");
        }

        if (!reader.GetBoolean(reader.GetOrdinal("state_exists")))
            return Rejected(item, "state.not-found", $"Calculation state for job {item.JobId} was not found.");

        var completedOrdinal = reader.GetOrdinal("last_completed_at");

        if (!reader.IsDBNull(completedOrdinal))
        {
            var lastCompletedAtUtc = ToUtcDateTimeOffset(reader.GetDateTime(completedOrdinal));

            if (lastCompletedAtUtc >= item.CompletedAtUtc)
            {
                return Rejected(
                    item,
                    "result.outdated",
                    $"Calculation result for job {item.JobId} is older than the stored result.");
            }
        }

        return Rejected(item, "state.update-rejected", $"Calculation state for job {item.JobId} was not updated.");
    }

    /// <summary>
    /// Читает состояния вместе с названием и оборудованием задания.
    /// </summary>
    private static async Task<IReadOnlyList<CalcJobStateDto>> ReadStatesAsync(NpgsqlConnection connection, long? jobId, CancellationToken ct)
    {
        const string sql = """
            SELECT state.job_id,
                   job.name AS job_name,
                   job.equipment_name,
                   state.status,
                   state.reason_code,
                   state.reason_message,
                   state.definition_version,
                   state.configuration_revision,
                   state.cycle_number,
                   state.success_count,
                   state.skipped_count,
                   state.error_count,
                   state.consecutive_skipped_count,
                   state.consecutive_error_count,
                   state.last_started_at,
                   state.last_completed_at,
                   state.last_success_at,
                   state.last_duration_ms,
                   state.last_inputs::text AS last_inputs,
                   state.last_outputs::text AS last_outputs,
                   state.updated_at
            FROM public.calc_job_state AS state
            INNER JOIN public.calc_job AS job ON job.id = state.job_id
            WHERE @job_id IS NULL OR state.job_id = @job_id
            ORDER BY job.sort_order, job.name, state.job_id;
            """;

        var result = new List<CalcJobStateDto>();

        await using var command = new NpgsqlCommand(sql, connection);
        AddNullable(command, "job_id", NpgsqlDbType.Bigint, jobId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new CalcJobStateDto
            {
                JobId = reader.GetInt64(reader.GetOrdinal("job_id")),
                JobName = reader.GetString(reader.GetOrdinal("job_name")),
                EquipmentName = ReadNullableString(reader, "equipment_name"),
                Status = ParseStatus(reader.GetString(reader.GetOrdinal("status"))),
                ReasonCode = ReadNullableString(reader, "reason_code"),
                ReasonMessage = ReadNullableString(reader, "reason_message"),
                DefinitionVersion = ReadNullableString(reader, "definition_version"),
                ConfigurationRevision = ReadNullableInt64(reader, "configuration_revision"),
                CycleNumber = reader.GetInt64(reader.GetOrdinal("cycle_number")),
                SuccessCount = reader.GetInt64(reader.GetOrdinal("success_count")),
                SkippedCount = reader.GetInt64(reader.GetOrdinal("skipped_count")),
                ErrorCount = reader.GetInt64(reader.GetOrdinal("error_count")),
                ConsecutiveSkippedCount = reader.GetInt64(reader.GetOrdinal("consecutive_skipped_count")),
                ConsecutiveErrorCount = reader.GetInt64(reader.GetOrdinal("consecutive_error_count")),
                LastStartedAtUtc = ReadNullableDateTimeOffset(reader, "last_started_at"),
                LastCompletedAtUtc = ReadNullableDateTimeOffset(reader, "last_completed_at"),
                LastSuccessAtUtc = ReadNullableDateTimeOffset(reader, "last_success_at"),
                LastDurationMs = ReadNullableInt64(reader, "last_duration_ms"),
                LastInputs = ReadNullableJson(reader, "last_inputs"),
                LastOutputs = ReadNullableJson(reader, "last_outputs"),
                UpdatedAtUtc = ToUtcDateTimeOffset(reader.GetDateTime(reader.GetOrdinal("updated_at")))
            });
        }

        return result;
    }

    private static CalcExecutionResultSaveResponse Rejected(CalcExecutionResultItemDto item, string errorCode, string errorMessage)
    {
        return new CalcExecutionResultSaveResponse
        {
            JobId = item.JobId,
            ServiceCycleNumber = item.ServiceCycleNumber,
            Accepted = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
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
            // Ошибка rollback не должна скрывать исходную ошибку.
        }
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static void AddJson(NpgsqlCommand command, string name, JsonElement value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = value.GetRawText();
    }

    private static CalcJobStateStatusDto ParseStatus(string value)
    {
        if (Enum.TryParse<CalcJobStateStatusDto>(value, true, out var result)
            && Enum.IsDefined(typeof(CalcJobStateStatusDto), result))
        {
            return result;
        }

        throw new InvalidOperationException($"Unsupported calculation state status '{value}' in PostgreSQL.");
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

    private static long? ReadNullableInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ToUtcDateTimeOffset(reader.GetDateTime(ordinal));
    }

    private static JsonElement? ReadNullableJson(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
            return null;

        using var document = JsonDocument.Parse(reader.GetString(ordinal));
        return document.RootElement.Clone();
    }

    private static DateTimeOffset ToUtcDateTimeOffset(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(value.ToUniversalTime());
    }
}