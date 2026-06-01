using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using TechMES.Application.EventLog;
using TechMES.Contracts.EventLog;

namespace TechMES.Infrastructure.PostgreSql.EventLog;

/// <summary>
/// PostgreSQL-хранилище журналов EventPicker:
/// Operation actions читаются из public."OperatorAct",
/// Alarm history читается из public.alarm_history.
/// Эти таблицы уже существуют и заполняются SCADA/WPF-логикой, WEB их только читает.
/// </summary>
public sealed class PostgreSqlEventLogStore : IEventLogStore
{
    /// <summary>
    /// Connection string к EventPicker PostgreSQL базе.
    /// </summary>
    private readonly string _connectionString;

    /// <summary>
    /// Создает хранилище EventLog и читает строку подключения из Runtime.Service appsettings.
    /// </summary>
    public PostgreSqlEventLogStore(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("EventPicker")
            ?? configuration["EventDatabase:ConnectionString"]
            ?? throw new InvalidOperationException(
                "EventPicker PostgreSQL connection string is not configured. " +
                "Set ConnectionStrings:EventPicker or EventDatabase:ConnectionString in Runtime.Service appsettings.json.");
    }

    /// <summary>
    /// Проверяет, можно ли открыть соединение с EventPicker БД.
    /// Используется health endpoint-ом.
    /// </summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Читает действия операторов за локальный календарный день.
    /// Фильтр применяется к Equip и Tag, чтобы можно было быстро найти события оборудования.
    /// </summary>
    public async Task<IReadOnlyList<OperatorActionDto>> GetOperatorActionsAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default)
    {
        var (fromUtc, toUtc) = BuildLocalDayRangeUtc(date);
        var filter = NormalizeFilter(equipmentFilter);
        var result = new List<OperatorActionDto>();

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                "Date",
                "Type",
                "Client",
                "User",
                "Tag",
                "Equip",
                "Desc",
                "OldV",
                "NewV"
            FROM public."OperatorAct"
            WHERE "Date" >= @from_utc
              AND "Date" < @to_utc
              AND (
                    @filter = ''
                    OR "Equip" ILIKE @like_filter
                    OR "Tag" ILIKE @like_filter
                  )
            ORDER BY "Date" DESC;
            """,
            conn);

        AddTimestampRange(cmd, fromUtc, toUtc);
        AddFilter(cmd, filter);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var timestamp = ReadDateTime(reader, "Date");

            result.Add(new OperatorActionDto
            {
                Timestamp = timestamp,
                Time = timestamp.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern),
                Type = ReadInt(reader, "Type"),
                Client = ReadString(reader, "Client"),
                User = ReadString(reader, "User"),
                Tag = ReadString(reader, "Tag"),
                Equip = ReadString(reader, "Equip"),
                Desc = ReadString(reader, "Desc"),
                OldValue = ReadString(reader, "OldV").ToUpperInvariant(),
                NewValue = ReadString(reader, "NewV").ToUpperInvariant()
            });
        }

        return result;
    }

    /// <summary>
    /// Читает историю тревог за локальный календарный день.
    /// Фильтр применяется к equipment.
    /// </summary>
    public async Task<IReadOnlyList<AlarmHistoryDto>> GetAlarmHistoryAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default)
    {
        var (fromUtc, toUtc) = BuildLocalDayRangeUtc(date);
        var filter = NormalizeFilter(equipmentFilter);
        var result = new List<AlarmHistoryDto>();

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                localtimedate,
                category,
                fullname,
                userlocation,
                equipment,
                item,
                desc_,
                logstate
            FROM public.alarm_history
            WHERE localtimedate >= @from_utc
              AND localtimedate < @to_utc
              AND (
                    @filter = ''
                    OR equipment ILIKE @like_filter
                  )
            ORDER BY localtimedate DESC;
            """,
            conn);

        AddTimestampRange(cmd, fromUtc, toUtc);
        AddFilter(cmd, filter);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var timestamp = ReadDateTime(reader, "localtimedate");

            result.Add(new AlarmHistoryDto
            {
                Timestamp = timestamp,
                Time = timestamp.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern),
                Category = CleanScadaText(ReadString(reader, "category")),
                User = ReadString(reader, "fullname"),
                Location = ReadString(reader, "userlocation"),
                Equipment = ReadString(reader, "equipment"),
                Item = ReadString(reader, "item"),
                Comment = ReadString(reader, "desc_"),
                State = ReadString(reader, "logstate")
            });
        }

        return result;
    }

    /// <summary>
    /// Открывает новое Npgsql-соединение. Каждый метод сам владеет своим connection lifetime.
    /// </summary>
    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Добавляет параметры UTC-диапазона в SQL-команду.
    /// </summary>
    private static void AddTimestampRange(
        NpgsqlCommand cmd,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        cmd.Parameters.Add("from_utc", NpgsqlDbType.TimestampTz).Value = fromUtc;
        cmd.Parameters.Add("to_utc", NpgsqlDbType.TimestampTz).Value = toUtc;
    }

    /// <summary>
    /// Добавляет исходный фильтр и ILIKE-вариант для SQL.
    /// </summary>
    private static void AddFilter(NpgsqlCommand cmd, string filter)
    {
        cmd.Parameters.AddWithValue("filter", filter);
        cmd.Parameters.AddWithValue("like_filter", $"%{filter}%");
    }

    /// <summary>
    /// Переводит локальную дату пользователя в UTC-диапазон [день; следующий день).
    /// Так корректно фильтруются timestamp with time zone значения в PostgreSQL.
    /// </summary>
    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) BuildLocalDayRangeUtc(DateTime date)
    {
        var startLocal = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, TimeZoneInfo.Local);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(1), TimeZoneInfo.Local);

        return (
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(endUtc, TimeSpan.Zero));
    }

    /// <summary>
    /// Безопасно читает DateTime/DateTimeOffset из Npgsql reader и возвращает локальное время.
    /// </summary>
    private static DateTime ReadDateTime(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
            return DateTime.MinValue;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto.LocalDateTime,
            DateTime dt => dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt,
            _ => DateTime.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTime.MinValue
        };
    }

    /// <summary>
    /// Читает строковое поле, заменяя null на пустую строку.
    /// </summary>
    private static string ReadString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? "";
    }

    /// <summary>
    /// Читает целочисленное поле, заменяя null на 0.
    /// </summary>
    private static int ReadInt(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Нормализует пользовательский фильтр.
    /// </summary>
    private static string NormalizeFilter(string? value)
    {
        return (value ?? "").Trim();
    }

    /// <summary>
    /// Очищает SCADA-localized текст вида @(...) перед отображением в WEB.
    /// </summary>
    private static string CleanScadaText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Trim();

        if (text.StartsWith("@(", StringComparison.Ordinal)
            && text.EndsWith(")", StringComparison.Ordinal)
            && text.Length >= 3)
        {
            text = text[2..^1].Trim();
        }

        return text
            .Replace("@(", "", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal)
            .Trim();
    }
}
