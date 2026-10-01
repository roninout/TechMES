using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Сохраняет последний снимок активных аварий. WEB читает его из памяти,
/// а чтение CiAdvancedAlarm через ODBC выполняется в одном фоновом задании.
/// </summary>
public sealed class CtApiActiveAlarmProvider : IActiveAlarmProvider
{
    private readonly ILogger<CtApiActiveAlarmProvider> _logger;
    private readonly string? _alarmOdbcConnectionString;
    private readonly object _stateLock = new();

    private ActiveAlarmsResponse? _snapshot;
    private Task? _refreshTask;
    private CancellationTokenSource? _scanStop;
    private DateTimeOffset _nextRefreshAt;
    private string? _refreshError;
    private long _version;

    public CtApiActiveAlarmProvider(IConfiguration configuration, ILogger<CtApiActiveAlarmProvider> logger)
    {
        _logger = logger;
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
    /// Получает полный снимок через ODBC и публикует его только после
    /// успешного завершения чтения. Пока оно идёт, WEB видит прежний снимок.
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var (items, readDurationMs) = await ReadCiAdvancedAlarmsAsync(ct);

            ct.ThrowIfCancellationRequested();
            var refreshDurationMs = watch.ElapsedMilliseconds;

            lock (_stateLock)
            {
                _snapshot = new ActiveAlarmsResponse
                {
                    Items = items,
                    LoadedAtUtc = DateTimeOffset.UtcNow,
                    ReadDurationMs = readDurationMs,
                    RefreshDurationMs = refreshDurationMs
                };

                _version++;
                _refreshError = null;
                _nextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogInformation("Active alarms refreshed from CiAdvancedAlarm. Count={Count}, ReadDurationMs={ReadDurationMs}, RefreshDurationMs={RefreshDurationMs}", items.Count, readDurationMs, refreshDurationMs);
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

        _logger.LogInformation(
            "CiAdvancedAlarm ODBC completed. Count={Count}, OpenMs={OpenMs}, ExecuteMs={ExecuteMs}, FetchAndMapMs={FetchAndMapMs}, ReadDurationMs={ReadDurationMs}",
            items.Count, openDurationMs, executeDurationMs, readDurationMs - openDurationMs - executeDurationMs, readDurationMs);

        return (items, readDurationMs);
    }

    /// <summary>
    /// Переводит строку ODBC в модель WEB-таблицы. Время возникновения берём
    /// из OnTime; ConditionActiveTime служит запасным полем. Значения ODBC
    /// считаем UTC и переводим в локальный часовой пояс Runtime Service.
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

        DateTime? LocalTimestamp(params string[] names)
        {
            foreach (var name in names)
            {
                if (!columns.TryGetValue(name, out var ordinal) || reader.IsDBNull(ordinal))
                    continue;

                var raw = reader.GetValue(ordinal);

                if (raw is DateTime timestamp && timestamp.Year > 1900)
                    return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime();

                if (DateTime.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Year > 1900)
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToLocalTime();
            }

            return null;
        }

        var tag = Value("FullName");

        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException("CiAdvancedAlarm returned a row without FullName.");

        var occurredAt = LocalTimestamp("OnTime", "ConditionActiveTime");
        var occurredAtText = occurredAt?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        var fields = new Dictionary<string, string>
        {
            ["FullName"] = tag,
            ["AlarmState"] = Value("AlarmState"),
            ["AlarmDesc"] = Value("AlarmDesc"),
            ["OnTime"] = Value("OnTime")
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
            AckDate = LocalTimestamp("AckTime")?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
            RawFields = fields
        };
    }
}