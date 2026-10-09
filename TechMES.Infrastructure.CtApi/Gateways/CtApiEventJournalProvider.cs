using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Events;
using TechMES.Contracts.Events;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Хранит готовый снимок CDBEventJournal для каждого диапазона.
/// Долгий ODBC-запрос выполняется в фоне, пока открытая WEB-страница ожидает результат.
/// </summary>
public sealed class CtApiEventJournalProvider : IEventJournalProvider
{
    private readonly record struct RangeKey(DateOnly From, DateOnly To);

    private sealed class RangeState
    {
        public List<EventJournalDto>? Items { get; set; }
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
    private readonly ILogger<CtApiEventJournalProvider> _logger;
    private readonly string? _connectionString;
    private readonly int _requestTimeoutSeconds;

    /// <summary>
    /// Использует уже настроенные в Maintenance ODBC-подключение и максимальное время запроса.
    /// </summary>
    public CtApiEventJournalProvider(IConfiguration configuration, ILogger<CtApiEventJournalProvider> logger)
    {
        _logger = logger;
        _connectionString = configuration["CtApi:AlarmOdbcConnectionString"];
        _requestTimeoutSeconds = int.TryParse(configuration["CtApi:AlarmOdbcRequestTimeoutSeconds"], out var seconds) && seconds > 0
            ? seconds
            : 120;
    }

    /// <summary>
    /// Сразу отдаёт последний снимок и запускает одно чтение периода, если снимка ещё нет
    /// или оператор запросил Refresh. Опрос WEB продлевает время работы фонового задания.
    /// </summary>
    public Task<EventJournalResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (to < from || to == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(to), "Invalid event journal date range.");

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

                    var readToken = state.Stop.Token;
                    state.ReadTask = Task.Run(() => ReadAndPublishAsync(key, state, readToken));
                }
            }

            if (state.Stop is not null && state.ReadTask is { IsCompleted: false } && !state.Stop.IsCancellationRequested)
                state.Stop.CancelAfter(TimeSpan.FromSeconds(12));

            var notModified = state.Items is not null && knownVersion == state.Version;

            return Task.FromResult(new EventJournalResponse
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
    /// Публикует только полностью прочитанный диапазон. При ошибке сохраняет предыдущий снимок.
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

            _logger.LogInformation("CDBEventJournal loaded from {From} to {To}. Count={Count}, DurationMs={DurationMs}", key.From, key.To, items.Count, durationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
                state.RetryAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("CDBEventJournal read from {From} to {To} stopped after WEB polling ended.", key.From, key.To);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                state.Error = state.Items is null
                    ? "Event journal could not be loaded. Runtime will retry."
                    : "Event journal refresh failed; the last available data is shown.";

                state.RetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogError(ex, "CDBEventJournal read failed from {From} to {To}.", key.From, key.To);
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
    /// Читает события между локальной полуночью From и началом дня после To.
    /// Границы переводятся в UTC, как в действующем провайдере истории аварий.
    /// </summary>
    private async Task<(List<EventJournalDto> Items, long DurationMs)> ReadRangeAsync(RangeKey key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("CtApi:AlarmOdbcConnectionString is not configured.");

        var localStart = DateTime.SpecifyKind(key.From.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(key.To.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, TimeZoneInfo.Local);
        var watch = Stopwatch.StartNew();
        var items = new List<EventJournalDto>();

        using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT "Time", Message, CustomStringField, Source, Category, ClientAddressDesc, "User", Id, AlarmStateDesc
        FROM CDBEventJournal
        WHERE "Time" >= ? AND "Time" < ?
        """;
        command.CommandTimeout = _requestTimeoutSeconds;
        command.Parameters.Add("fromUtc", OdbcType.DateTime).Value = utcStart;
        command.Parameters.Add("toUtc", OdbcType.DateTime).Value = utcEnd;

        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            items.Add(new EventJournalDto
            {
                Date = ReadLocalTimestamp(reader, 0),
                Description = ReadText(reader, 1),
                CustomStringField = ReadText(reader, 2),
                Source = ReadText(reader, 3),
                Category = ReadText(reader, 4),
                ClientAddressDesc = ReadText(reader, 5),
                User = ReadText(reader, 6),
                Id = reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                AlarmStateDesc = ReadText(reader, 8)
            });
        }

        items.Sort((left, right) => Nullable.Compare(right.Date, left.Date));
        return (items, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Преобразует UTC TIMESTAMP ODBC в локальное время Runtime.
    /// </summary>
    private static DateTime? ReadLocalTimestamp(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetDateTime(ordinal);
        return value.Year > 1900 ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime() : null;
    }

    /// <summary>
    /// Возвращает пустую строку для NULL и текстовое значение для остальных типов ODBC.
    /// </summary>
    private static string ReadText(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    }
}