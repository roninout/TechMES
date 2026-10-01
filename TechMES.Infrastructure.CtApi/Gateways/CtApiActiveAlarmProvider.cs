using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;
using TechMES.Infrastructure.CtApi.Native;
using System.Data.Common;
using System.Data.Odbc;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Сохраняет последний снимок активных аварий. WEB читает его из памяти,
/// а длительный обход CtApi выполняется в одном фоновом задании.
/// </summary>
public sealed class CtApiActiveAlarmProvider : IActiveAlarmProvider
{
    private const int MaxRows = 5000;
    private static readonly string[] Properties = ["TAG", "NAME", "DESC", "AREA", "CATEGORY", "ONDATEEXT", "ONDATE", "ONTIME", "OFFDATE", "ACKDATE"];
    private static readonly string[] DateTimeFormats = ["dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss", "dd/MM/yyyy HH:mm", "d/M/yyyy H:mm", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy H:mm:ss", "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm"];

    private readonly ICtApiNativeClient _client;
    private readonly ILogger<CtApiActiveAlarmProvider> _logger;
    private readonly int _area;
    private readonly bool _useLegacyActiveAlarmQuery;
    private readonly string? _alarmOdbcConnectionString;
    private readonly object _stateLock = new();

    private ActiveAlarmsResponse? _snapshot;
    private Task? _refreshTask;
    private CancellationTokenSource? _scanStop;
    private DateTimeOffset _nextRefreshAt;
    private string? _refreshError;
    private long _version;

    public CtApiActiveAlarmProvider(ICtApiNativeClient client, IConfiguration configuration, ILogger<CtApiActiveAlarmProvider> logger)
    {
        _client = client;
        _logger = logger;
        _area = configuration.GetValue("CtApi:AlarmArea", -1);
        _useLegacyActiveAlarmQuery = configuration.GetValue("CtApi:UseLegacyActiveAlarmQuery", false);
        _alarmOdbcConnectionString = configuration["CtApi:AlarmOdbcConnectionString"];
    }

    /// <summary>
    /// Возвращает последний завершённый снимок немедленно. Новый обход начинается
    /// через 30 секунд после предыдущего или по ручному Refresh.
    /// Открытая страница продлевает выполняющийся обход при каждом опросе.
    /// </summary>
    public Task<ActiveAlarmsResponse> GetActiveAsync(bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if ((_refreshTask is null || _refreshTask.IsCompleted) && (forceRefresh || DateTimeOffset.UtcNow >= _nextRefreshAt))
            {
                _nextRefreshAt = DateTimeOffset.MaxValue;
                _refreshError = null;
                _scanStop = new CancellationTokenSource();
                _scanStop.CancelAfter(TimeSpan.FromSeconds(12));

                var scanToken = _scanStop.Token;
                _refreshTask = Task.Run(() => RefreshCoreAsync(scanToken));
            }

            // Если страница перестала опрашивать Runtime, обход отменится.
            // Несколько открытых страниц продлевают один общий обход.
            if (_scanStop is not null && _refreshTask is { IsCompleted: false } && !_scanStop.IsCancellationRequested)
                _scanStop.CancelAfter(TimeSpan.FromSeconds(12));

            var notModified = _snapshot is not null && knownVersion == _version;

            return Task.FromResult(new ActiveAlarmsResponse
            {
                Items = notModified ? [] : (_snapshot?.Items ?? []),
                LoadedAtUtc = _snapshot?.LoadedAtUtc,
                Truncated = _snapshot?.Truncated ?? false,
                IsRefreshing = _refreshTask is { IsCompleted: false },
                RefreshError = _refreshError,
                Version = _version,
                NotModified = notModified,
                ReadDurationMs = _snapshot?.ReadDurationMs,
                RefreshDurationMs = _snapshot?.RefreshDurationMs
            });
        }
    }

    /// <summary>
    /// Получает полный снимок выбранным способом и публикует его только после
    /// успешного завершения чтения. Пока оно идёт, WEB видит прежний снимок.
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            List<ActiveAlarmDto> items;
            long readDurationMs;
            var truncated = false;

            if (_useLegacyActiveAlarmQuery)
            {
                var (rows, wasTruncated) = await ReadCtApiAlarmAsync(ct);
                EnsureNames(rows);
                items = rows.Select(ToAlarm).OrderByDescending(alarm => alarm.OccurredAt).ToList();
                truncated = wasTruncated;
                readDurationMs = watch.ElapsedMilliseconds;
            }
            else
            {
                var result = await ReadCiAdvancedAlarmsAsync(ct);
                items = result.Items;
                readDurationMs = result.ReadDurationMs;
            }

            ct.ThrowIfCancellationRequested();
            var refreshDurationMs = watch.ElapsedMilliseconds;

            lock (_stateLock)
            {
                _snapshot = new ActiveAlarmsResponse
                {
                    Items = items,
                    Truncated = truncated,
                    LoadedAtUtc = DateTimeOffset.UtcNow,
                    ReadDurationMs = readDurationMs,
                    RefreshDurationMs = refreshDurationMs
                };

                _version++;
                _refreshError = null;
                _nextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogInformation(
                "Active alarms refreshed. Source={Source}, Count={Count}, Truncated={Truncated}, ReadDurationMs={ReadDurationMs}, RefreshDurationMs={RefreshDurationMs}",
                _useLegacyActiveAlarmQuery ? "CTAPIAlarm" : "CiAdvancedAlarm",
                items.Count,
                truncated,
                readDurationMs,
                refreshDurationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                _nextRefreshAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation("Active alarm scan stopped after {DurationMs} ms because the page is no longer polling.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _refreshError = _snapshot is null
                    ? "The alarm list could not be loaded. Runtime will retry."
                    : "Alarm refresh failed; the last available data is shown.";

                _nextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogError(ex, "Active alarm refresh failed after {DurationMs} ms.", watch.ElapsedMilliseconds);
        }
        finally
        {
            lock (_stateLock)
            {
                _scanStop?.Dispose();
                _scanStop = null;
            }
        }
    }

    /// <summary>
    /// Читает текущие аварии из CiAdvancedAlarm одним запросом. В проверенной
    /// на данном сервере выборке AlarmState=0 означает Normal. Текст состояния
    /// получаем из AlarmDesc, не преобразуя числовые коды в приложении.
    /// </summary>
    private async Task<(List<ActiveAlarmDto> Items, long ReadDurationMs)> ReadCiAdvancedAlarmsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_alarmOdbcConnectionString))
            throw new InvalidOperationException("CtApi:AlarmOdbcConnectionString is not configured.");

        var watch = Stopwatch.StartNew();
        using var connection = new OdbcConnection(_alarmOdbcConnectionString);
        await connection.OpenAsync(ct);
        var openDurationMs = watch.ElapsedMilliseconds;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FullName, Comment, AlarmCategory, AlarmState, AlarmDesc, OnTime, ConditionActiveTime, AckTime FROM CiAdvancedAlarm WHERE AlarmState <> 0";
        command.CommandTimeout = 120;

        using var reader = await command.ExecuteReaderAsync(ct);
        var executeDurationMs = watch.ElapsedMilliseconds - openDurationMs;
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);

        if (!columns.ContainsKey("FullName") || !columns.ContainsKey("AlarmState"))
            throw new InvalidOperationException("CiAdvancedAlarm must return FullName and AlarmState columns.");

        var items = new List<ActiveAlarmDto>();

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            items.Add(ToCiAdvancedAlarm(reader, columns));
        }

        items.Sort((left, right) => Nullable.Compare(right.OccurredAt, left.OccurredAt));
        var readDurationMs = watch.ElapsedMilliseconds;

        _logger.LogInformation("CiAdvancedAlarm ODBC completed. Count={Count}, OpenMs={OpenMs}, ExecuteMs={ExecuteMs}, FetchAndMapMs={FetchAndMapMs}, ReadDurationMs={ReadDurationMs}",
            items.Count,
            openDurationMs,
            executeDurationMs,
            readDurationMs - openDurationMs - executeDurationMs,
            readDurationMs);

        return (items, readDurationMs);
    }

    /// <summary>
    /// Переводит строку ODBC в существующую модель WEB-таблицы. Время
    /// возникновения берём из OnTime; ConditionActiveTime служит запасным полем.
    /// </summary>
    private static ActiveAlarmDto ToCiAdvancedAlarm(DbDataReader reader, IReadOnlyDictionary<string, int> columns)
    {
        string Value(params string[] names)
        {
            foreach (var name in names)
            {
                if (columns.TryGetValue(name, out var ordinal) && !reader.IsDBNull(ordinal))
                    return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
            }

            return "";
        }

        DateTime? Timestamp(params string[] names)
        {
            foreach (var name in names)
            {
                if (!columns.TryGetValue(name, out var ordinal) || reader.IsDBNull(ordinal))
                    continue;

                var raw = reader.GetValue(ordinal);

                if (raw is DateTime timestamp)
                    return timestamp.Year > 1900 ? timestamp : null;

                if (DateTime.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return parsed.Year > 1900 ? parsed : null;
            }

            return null;
        }

        var tag = Value("FullName");
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException("CiAdvancedAlarm returned a row without FullName.");

        var occurredAt = Timestamp("OnTime", "ConditionActiveTime");
        var occurredAtText = occurredAt?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        var fields = new Dictionary<string, string>
        {
            ["FullName"] = tag,
            ["AlarmState"] = Value("AlarmState"),
            ["AlarmDesc"] = Value("AlarmDesc"),
            ["OnTime"] = occurredAtText
        };

        return new ActiveAlarmDto
        {
            Tag = tag,
            Description = Value("Comment"),
            Category = Value("AlarmCategory"),
            State = Value("AlarmDesc"),
            OccurredAt = occurredAt,
            OccurredAtText = occurredAtText,
            OnDate = occurredAt?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "",
            AckDate = Timestamp("AckTime")?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
            RawFields = fields
        };
    }

    /// <summary>
    /// Прежний способ чтения полной сводки. Остаётся в проекте для сравнения
    /// и возврата через CtApi:UseLegacyActiveAlarmQuery=true.
    /// </summary>
    private async Task<(IReadOnlyList<Dictionary<string, string>> Rows, bool Truncated)> ReadCtApiAlarmAsync(CancellationToken ct)
    {
        var query = $"CTAPIAlarm(0,0,{_area})";
        var watch = Stopwatch.StartNew();
        var result = await _client.FindAlarmsAsync(query, MaxRows, Properties, ct);

        _logger.LogInformation("CTAPIAlarm read completed. RawCount={Count}, Truncated={Truncated}, ReadDurationMs={DurationMs}", result.Rows.Count, result.Truncated, watch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Экспериментально читает строки таблицы AlarmSummary через тот же native
    /// цикл ctFindFirstEx/ctFindNext/ctGetProperty и отдельно измеряет чтение.
    /// </summary>
    private async Task<(IReadOnlyList<Dictionary<string, string>> Rows, bool Truncated)> ReadAlarmSummaryAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = await _client.FindAlarmsAsync("AlarmSummary", MaxRows, Properties, ct);

        _logger.LogInformation("AlarmSummary read completed. RawCount={Count}, Truncated={Truncated}, ReadDurationMs={DurationMs}", result.Rows.Count, result.Truncated, watch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Оставляет ON-аварии и неквитированные OFF-аварии, возникшие вчера или
    /// сегодня по локальному времени Runtime. Квитированные OFF исключаются.
    /// </summary>
    private static List<ActiveAlarmDto> BuildAlarmSummaryItems(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var tomorrow = DateTime.Today.AddDays(1);

        return rows.Where(row => !IsSet(Get(row, "OFFDATE")) || !IsSet(Get(row, "ACKDATE")))
            .Select(ToAlarm)
            .Where(alarm => alarm.OccurredAt >= yesterday && alarm.OccurredAt < tomorrow)
            .OrderByDescending(alarm => alarm.OccurredAt)
            .ToList();
    }

    /// <summary>
    /// Пустые имена обычно означают неверные поля CtApi; такой ответ
    /// не подменяет последний корректный снимок пустым списком.
    /// </summary>
    private static void EnsureNames(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        if (rows.Any(row => string.IsNullOrWhiteSpace(Get(row, "TAG")) && string.IsNullOrWhiteSpace(Get(row, "NAME"))))
            throw new InvalidOperationException("CtApi returned an alarm without TAG/NAME. Verify alarm property names.");
    }

    /// <summary>
    /// CTAPIAlarm с Type=0 уже возвращает активные аварии. OFFDATE
    /// не исключает неквитированную строку из этого списка.
    /// </summary>
    private static ActiveAlarmDto ToAlarm(Dictionary<string, string> row)
    {
        var date = Get(row, "ONDATEEXT");
        if (string.IsNullOrWhiteSpace(date))
            date = Get(row, "ONDATE");

        var occurredAtText = $"{date} {Get(row, "ONTIME")}".Trim();
        var occurredAt = DateTime.TryParseExact(occurredAtText, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        var acknowledged = IsSet(Get(row, "ACKDATE"));
        var off = IsSet(Get(row, "OFFDATE"));

        return new ActiveAlarmDto
        {
            Tag = string.IsNullOrWhiteSpace(Get(row, "TAG")) ? Get(row, "NAME") : Get(row, "TAG"),
            Description = Get(row, "DESC"),
            Area = Get(row, "AREA"),
            Category = Get(row, "CATEGORY"),
            State = off ? "OFF / unacknowledged" : acknowledged ? "ON / acknowledged" : "ON / unacknowledged",
            OccurredAt = occurredAt,
            OccurredAtText = occurredAtText,
            OnDate = Get(row, "ONDATE"),
            AckDate = Get(row, "ACKDATE"),
            RawFields = row
        };
    }

    /// <summary>
    /// Неприменимые даты CtApi могут передаваться пустой строкой или нулём.
    /// </summary>
    private static bool IsSet(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim() != "0" && value.Trim() != "00/00/0000";
    }

    /// <summary>
    /// Сохраняет отсутствие необязательного свойства как пустое значение.
    /// </summary>
    private static string Get(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : "";
    }
}