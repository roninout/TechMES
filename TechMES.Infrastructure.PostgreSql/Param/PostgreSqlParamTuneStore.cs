using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Application.Param;
using TechMES.Contracts.Param;

namespace TechMES.Infrastructure.PostgreSql.Param;

/// <summary>
/// PostgreSQL-хранилище настроек PID Tune для VGA-оборудования.
/// </summary>
public sealed class PostgreSqlParamTuneStore : IParamTuneStore
{
    private readonly string _connectionString;
    private bool _schemaEnsured;

    public PostgreSqlParamTuneStore(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");
    }

    public async Task<ParamTuneSettingsResponse?> GetAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var name = NormalizeEquipmentName(equipmentName);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        await EnsureSchemaAsync(ct);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = """
        SELECT equipment_name, pv, pv_min, pv_max, sp, sp_min, sp_max, kp, ti, td, updated_at
        FROM public.equip_param_tune
        WHERE equipment_name = @equipment_name
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("equipment_name", name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<ParamTuneSettingsResponse> SaveAsync(
        string equipmentName,
        ParamTuneSaveRequest request,
        CancellationToken ct = default)
    {
        var name = NormalizeEquipmentName(equipmentName);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Equipment name is required.", nameof(equipmentName));

        await EnsureSchemaAsync(ct);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = """
        INSERT INTO public.equip_param_tune
            (equipment_name, pv, pv_min, pv_max, sp, sp_min, sp_max, kp, ti, td, updated_at)
        VALUES
            (@equipment_name, @pv, @pv_min, @pv_max, @sp, @sp_min, @sp_max, @kp, @ti, @td, now())
        ON CONFLICT (equipment_name) DO UPDATE SET
            pv = EXCLUDED.pv,
            pv_min = EXCLUDED.pv_min,
            pv_max = EXCLUDED.pv_max,
            sp = EXCLUDED.sp,
            sp_min = EXCLUDED.sp_min,
            sp_max = EXCLUDED.sp_max,
            kp = EXCLUDED.kp,
            ti = EXCLUDED.ti,
            td = EXCLUDED.td,
            updated_at = now()
        RETURNING equipment_name, pv, pv_min, pv_max, sp, sp_min, sp_max, kp, ti, td, updated_at
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("equipment_name", name);
        command.Parameters.AddWithValue("pv", (object?)NormalizeText(request.Pv) ?? DBNull.Value);
        command.Parameters.AddWithValue("pv_min", (object?)request.PvMin ?? DBNull.Value);
        command.Parameters.AddWithValue("pv_max", (object?)request.PvMax ?? DBNull.Value);
        command.Parameters.AddWithValue("sp", (object?)NormalizeText(request.Sp) ?? DBNull.Value);
        command.Parameters.AddWithValue("sp_min", (object?)request.SpMin ?? DBNull.Value);
        command.Parameters.AddWithValue("sp_max", (object?)request.SpMax ?? DBNull.Value);
        command.Parameters.AddWithValue("kp", (object?)request.Kp ?? DBNull.Value);
        command.Parameters.AddWithValue("ti", (object?)request.Ti ?? DBNull.Value);
        command.Parameters.AddWithValue("td", (object?)request.Td ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return Read(reader);

        return new ParamTuneSettingsResponse { EquipmentName = name };
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaEnsured)
            return;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = """
        CREATE TABLE IF NOT EXISTS public.equip_param_tune (
            equipment_name text PRIMARY KEY,
            pv text NULL,
            pv_min double precision NULL,
            pv_max double precision NULL,
            sp text NULL,
            sp_min double precision NULL,
            sp_max double precision NULL,
            kp double precision NULL,
            ti double precision NULL,
            td double precision NULL,
            updated_at timestamp without time zone NOT NULL DEFAULT now()
        );
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);

        _schemaEnsured = true;
    }

    private static ParamTuneSettingsResponse Read(NpgsqlDataReader reader) => new()
    {
        EquipmentName = reader.GetString(reader.GetOrdinal("equipment_name")),
        Pv = ReadString(reader, "pv"),
        PvMin = ReadNullableDouble(reader, "pv_min"),
        PvMax = ReadNullableDouble(reader, "pv_max"),
        Sp = ReadString(reader, "sp"),
        SpMin = ReadNullableDouble(reader, "sp_min"),
        SpMax = ReadNullableDouble(reader, "sp_max"),
        Kp = ReadNullableDouble(reader, "kp"),
        Ti = ReadNullableDouble(reader, "ti"),
        Td = ReadNullableDouble(reader, "td"),
        UpdatedAt = ReadNullableDateTime(reader, "updated_at")
    };

    private static string NormalizeEquipmentName(string value) => (value ?? "").Trim();

    private static string? NormalizeText(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? ReadString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? ReadNullableDouble(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static DateTime? ReadNullableDateTime(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }
}
