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
/// Хранит отдельный снимок истории для каждого диапазона дат.
/// Долгое ODBC-чтение выполняется в фоне, пока WEB опрашивает Runtime.
/// </summary>
public sealed class CtApiAlarmSummaryProvider : IAlarmSummaryProvider
{
    private readonly record struct RangeKey(DateOnly From, DateOnly To);

    private sealed class RangeState
    {
        public List<AlarmSummaryDto>? Items { get; set; }
        public Task? ReadTask { get; set; }
        public CancellationTokenSource? Stop { get; set; }
        public DateTimeOffset? LoadedAtUtc { get; set; }
        public DateTimeOffset LastPollAt { get; set; }
        public DateTimeOffset RetryAt { get; set; }
        public string? Error { get; set; }
        public long Version { get; set; }
        public long? ReadDurationMs { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<RangeKey, RangeState> _ranges = [];
    private readonly ILogger<CtApiAlarmSummaryProvider> _logger;
    private readonly string? _connectionString;
    private readonly int _requestTimeoutSeconds;
    private readonly int _refreshPeriodSeconds;

    /// <summary>
    /// Использует настройки ODBC, редактируемые в Maintenance.
    /// История и активные аварии получают интервал из одного параметра Runtime.
    /// </summary>
    public CtApiAlarmSummaryProvider(IConfiguration configuration, ILogger<CtApiAlarmSummaryProvider> logger)
    {
        _logger = logger;
        _connectionString = configuration["CtApi:AlarmOdbcConnectionString"];
        _requestTimeoutSeconds = int.TryParse(configuration["CtApi:AlarmOdbcRequestTimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0
            ? timeoutSeconds
            : 120;
        _refreshPeriodSeconds = int.TryParse(configuration["CtApi:AlarmOdbcRefreshPeriodSeconds"], out var refreshSeconds) && refreshSeconds > 0
            ? refreshSeconds
            : 30;
    }

    /// <summary>
    /// Немедленно возвращает последний завершённый снимок и при необходимости
    /// запускает одно фоновое чтение выбранного диапазона.
    /// Вместе со снимком передаёт WEB настроенный интервал автообновления.
    /// </summary>
    public Task<AlarmSummaryResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (to < from || to == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(to), "Invalid alarm history date range.");

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var oldKey in _ranges.Where(pair => now - pair.Value.LastPollAt > TimeSpan.FromMinutes(15)
                && pair.Value.ReadTask is not { IsCompleted: false }).Select(pair => pair.Key).ToArray())
            {
                _ranges.Remove(oldKey);
            }

            var key = new RangeKey(from, to);

            if (!_ranges.TryGetValue(key, out var state))
            {
                state = new RangeState { LastPollAt = now };
                _ranges.Add(key, state);
            }

            state.LastPollAt = now;

            if (state.ReadTask is null || state.ReadTask.IsCompleted)
            {
                if (forceRefresh || (state.Items is null && now >= state.RetryAt))
                {
                    state.Error = null;
                    state.Stop = new CancellationTokenSource();
                    state.Stop.CancelAfter(TimeSpan.FromSeconds(12));

                    var scanToken = state.Stop.Token;
                    state.ReadTask = Task.Run(() => ReadAndPublishAsync(key, state, scanToken));
                }
            }

            // Открытая страница продлевает чтение. После закрытия страницы
            // задание отменяется, если драйвер ODBC поддерживает отмену.
            if (state.Stop is not null && state.ReadTask is { IsCompleted: false } && !state.Stop.IsCancellationRequested)
                state.Stop.CancelAfter(TimeSpan.FromSeconds(12));

            var notModified = state.Items is not null && knownVersion == state.Version;

            return Task.FromResult(new AlarmSummaryResponse
            {
                Items = notModified ? [] : state.Items ?? [],
                LoadedAtUtc = state.LoadedAtUtc,
                IsRefreshing = state.ReadTask is { IsCompleted: false },
                RefreshError = state.Error,
                Version = state.Version,
                NotModified = notModified,
                ReadDurationMs = state.ReadDurationMs,
                RefreshPeriodSeconds = _refreshPeriodSeconds
            });
        }
    }

    /// <summary>
    /// Публикует новый снимок только после завершения обоих ODBC-запросов.
    /// Ошибка оставляет прежний снимок выбранного диапазона доступным WEB.
    /// </summary>
    private async Task ReadAndPublishAsync(RangeKey key, RangeState state, CancellationToken ct)
    {
        try
        {
            var (items, durationMs) = await ReadRangeAsync(key, ct);
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                state.Items = items;
                state.LoadedAtUtc = DateTimeOffset.UtcNow;
                state.ReadDurationMs = durationMs;
                state.Error = null;
                state.Version++;
            }

            _logger.LogInformation("CDBAlarmSummary loaded from {From} to {To}. Count={Count}, DurationMs={DurationMs}", key.From, key.To, items.Count, durationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
                state.RetryAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("CDBAlarmSummary read from {From} to {To} stopped after WEB polling ended.", key.From, key.To);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                state.Error = state.Items is null
                    ? "Alarm history could not be loaded. Runtime will retry."
                    : "Alarm history refresh failed; the last available data is shown.";

                state.RetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogError(ex, "CDBAlarmSummary read failed from {From} to {To}.", key.From, key.To);
        }
        finally
        {
            lock (_gate)
            {
                state.Stop?.Dispose();
                state.Stop = null;
            }
        }
    }

    /// <summary>
    /// Читает определения приоритетов и историю одним подключением ODBC.
    /// Дни диапазона включительны; верхняя граница SQL — начало дня после To.
    /// По принятому сейчас правилу TIMESTAMP в ODBC содержит время UTC.
    /// </summary>
    private async Task<(List<AlarmSummaryDto> Items, long DurationMs)> ReadRangeAsync(RangeKey key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("CtApi:AlarmOdbcConnectionString is not configured.");

        var localStart = DateTime.SpecifyKind(key.From.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(key.To.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, TimeZoneInfo.Local);
        var watch = Stopwatch.StartNew();

        using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Исторические записи могут относиться к уже неактивным авариям,
        // поэтому справочник приоритетов читаем без фильтра по состоянию.
        var priorities = await ReadPrioritiesAsync(connection, ct);
        var items = new List<AlarmSummaryDto>();

        using var command = connection.CreateCommand();
        command.CommandText = """
    SELECT RecordId, Source, CustomStringField2, StateDesc, SeverityDesc, SeverityValue,
           ActiveTime, InactiveTime, CustomNumericField13, AckTime, AckUserName, ClientAddressDesc, Id
    FROM CDBAlarmSummary
    WHERE ActiveTime >= ? AND ActiveTime < ?
    """;
        command.CommandTimeout = _requestTimeoutSeconds;
        command.Parameters.Add("fromUtc", OdbcType.DateTime).Value = utcStart;
        command.Parameters.Add("toUtc", OdbcType.DateTime).Value = utcEnd;

        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var tag = ReadText(reader, 1).Trim();
            var severityValue = reader.IsDBNull(5)
                ? (int?)null
                : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
            var duration = reader.IsDBNull(8)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture);
            var id = reader.IsDBNull(12)
                ? (int?)null
                : Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture);

            items.Add(new AlarmSummaryDto
            {
                RecordId = ReadText(reader, 0),
                Id = id,
                Tag = tag,
                Description = CleanDescription(ReadText(reader, 2)),
                StateDesc = ReadText(reader, 3),
                Severity = ReadText(reader, 4),
                SeverityValue = severityValue,
                Priority = priorities.TryGetValue(tag, out var priority) ? priority : null,
                ActiveAt = ReadLocalTimestamp(reader, 6),
                InactiveAt = ReadLocalTimestamp(reader, 7),
                Duration = duration,
                AckAt = ReadLocalTimestamp(reader, 9),
                AckUserName = ReadText(reader, 10).Trim(),
                ClientAddressDesc = ReadText(reader, 11).Trim()
            });
        }

        items.Sort((left, right) => Nullable.Compare(right.ActiveAt, left.ActiveAt));
        return (items, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Создаёт словарь FullName → Priority. Отсутствующий тег или пустое
    /// значение приоритета позднее отображаются как Unknown.
    /// </summary>
    private async Task<Dictionary<string, int?>> ReadPrioritiesAsync(OdbcConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FullName, Priority FROM CiAdvancedAlarm";
        command.CommandTimeout = _requestTimeoutSeconds;

        using var reader = await command.ExecuteReaderAsync(ct);
        var priorities = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var tag = ReadText(reader, 0).Trim();

            if (tag.Length == 0)
                continue;

            var priorityText = ReadText(reader, 1);
            var priority = int.TryParse(priorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;

            priorities[tag] = priority;
        }

        _logger.LogInformation("CiAdvancedAlarm priorities loaded. Tags={Count}", priorities.Count);
        return priorities;
    }

    /// <summary>
    /// Переводит TIMESTAMP из ODBC в локальное время.
    /// Пустую дату и служебные значения без реального времени не показывает.
    /// </summary>
    private static DateTime? ReadLocalTimestamp(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetDateTime(ordinal);
        return value.Year > 1900
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
            : null;
    }

    /// <summary>
    /// Безопасно преобразует nullable поле ODBC в строку.
    /// </summary>
    private static string ReadText(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// Удаляет только внешнюю пару @(...) из CustomStringField2.
    /// </summary>
    private static string CleanDescription(string value)
    {
        var text = value.Trim();

        return text.StartsWith("@(", StringComparison.Ordinal) && text.EndsWith(')') ? text[2..^1].Trim() : text;
    }
}