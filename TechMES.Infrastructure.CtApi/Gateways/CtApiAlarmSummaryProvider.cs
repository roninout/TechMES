using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Хранит отдельный снимок истории для каждого запрошенного дня.
/// Чтение ODBC продолжается, пока открытая страница опрашивает Runtime.
/// </summary>
public sealed class CtApiAlarmSummaryProvider : IAlarmSummaryProvider
{
    private sealed class DayState
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
    private readonly Dictionary<DateOnly, DayState> _days = [];
    private readonly ILogger<CtApiAlarmSummaryProvider> _logger;
    private readonly string? _connectionString;
    private readonly int _requestTimeoutSeconds;

    /// <summary>
    /// Использует ту же строку подключения и тот же максимальный timeout,
    /// которые уже редактируются в Maintenance для активных аварий.
    /// </summary>
    public CtApiAlarmSummaryProvider(IConfiguration configuration, ILogger<CtApiAlarmSummaryProvider> logger)
    {
        _logger = logger;
        _connectionString = configuration["CtApi:AlarmOdbcConnectionString"];
        _requestTimeoutSeconds = int.TryParse(configuration["CtApi:AlarmOdbcRequestTimeoutSeconds"], out var seconds) && seconds > 0
            ? seconds
            : 120;
    }

    /// <summary>
    /// Возвращает готовый снимок немедленно и при необходимости запускает чтение дня.
    /// Дни, к которым давно не обращались, удаляются из памяти после завершения задания.
    /// </summary>
    public Task<AlarmSummaryResponse> GetAsync(DateOnly date, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var oldDate in _days.Where(pair => now - pair.Value.LastPollAt > TimeSpan.FromMinutes(15)
                && pair.Value.ReadTask is not { IsCompleted: false }).Select(pair => pair.Key).ToArray())
            {
                _days.Remove(oldDate);
            }

            if (!_days.TryGetValue(date, out var state))
            {
                state = new DayState { LastPollAt = now };
                _days.Add(date, state);
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
                    state.ReadTask = Task.Run(() => ReadAndPublishAsync(date, state, scanToken));
                }
            }

            // Когда страница закрывается, этот вызов прекращается; незавершённое
            // чтение будет отменено после 12 секунд без опроса.
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
                ReadDurationMs = state.ReadDurationMs
            });
        }
    }

    /// <summary>
    /// Заменяет снимок только после полного успешного чтения выбранного дня.
    /// Ошибка сохраняет предыдущие строки для этой даты.
    /// </summary>
    private async Task ReadAndPublishAsync(DateOnly date, DayState state, CancellationToken ct)
    {
        try
        {
            var (items, durationMs) = await ReadDayAsync(date, ct);
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                state.Items = items;
                state.LoadedAtUtc = DateTimeOffset.UtcNow;
                state.ReadDurationMs = durationMs;
                state.Error = null;
                state.Version++;
            }

            _logger.LogInformation("CDBAlarmSummary loaded for {Date}. Count={Count}, DurationMs={DurationMs}", date, items.Count, durationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
                state.RetryAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("CDBAlarmSummary read for {Date} stopped because the page is no longer polling.", date);
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

            _logger.LogError(ex, "CDBAlarmSummary read failed for {Date}.", date);
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
    /// Читает только один локальный день. Параметры ODBC позиционные:
    /// начало дня включено, начало следующего дня исключено.
    /// Как и у активных аварий, значения TIMESTAMP считаются временем UTC.
    /// </summary>
    private async Task<(List<AlarmSummaryDto> Items, long DurationMs)> ReadDayAsync(DateOnly date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("CtApi:AlarmOdbcConnectionString is not configured.");

        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, TimeZoneInfo.Local);
        var watch = Stopwatch.StartNew();

        using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RecordId, Source, CustomStringField2, SeverityDesc, SeverityValue, ActiveTime FROM CDBAlarmSummary WHERE ActiveTime >= ? AND ActiveTime < ?";
        command.CommandTimeout = _requestTimeoutSeconds;
        command.Parameters.Add("fromUtc", OdbcType.DateTime).Value = utcStart;
        command.Parameters.Add("toUtc", OdbcType.DateTime).Value = utcEnd;

        using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<AlarmSummaryDto>();

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var rawDate = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            var activeAt = rawDate is { Year: > 1900 } ? DateTime.SpecifyKind(rawDate.Value, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;

            var severityValue = reader.IsDBNull(4)
                ? (int?)null
                : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);

            items.Add(new AlarmSummaryDto
            {
                RecordId = ReadText(reader, 0),
                Tag = ReadText(reader, 1),
                Description = CleanDescription(ReadText(reader, 2)),
                Severity = ReadText(reader, 3),
                SeverityValue = severityValue,
                ActiveAt = activeAt
            });
        }

        items.Sort((left, right) => Nullable.Compare(right.ActiveAt, left.ActiveAt));
        return (items, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Преобразует NULL в пустую строку без изменения текста остальных полей.
    /// </summary>
    private static string ReadText(System.Data.Common.DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// Удаляет только внешнюю пару @(...) из CustomStringField2.
    /// Скобки внутри самого описания остаются на месте.
    /// </summary>
    private static string CleanDescription(string value)
    {
        var text = value.Trim();

        return text.StartsWith("@(", StringComparison.Ordinal) && text.EndsWith(')')
            ? text[2..^1].Trim()
            : text;
    }
}