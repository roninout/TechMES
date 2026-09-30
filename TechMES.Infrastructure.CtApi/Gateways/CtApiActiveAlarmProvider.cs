using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;
using TechMES.Infrastructure.CtApi.Native;

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
                NotModified = notModified
            });
        }
    }

    /// <summary>
    /// Получает полный снимок одним запросом CtApi и публикует его только после
    /// успешного окончания обхода. Пока он выполняется, WEB видит прежний снимок.
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var query = $"CTAPIAlarm(0,0,{_area})";
            var (rows, truncated) = await _client.FindAlarmsAsync(query, MaxRows, Properties, ct);
            EnsureNames(rows);

            var items = rows.Select(ToAlarm).OrderByDescending(alarm => alarm.OccurredAt).ToList();

            lock (_stateLock)
            {
                _snapshot = new ActiveAlarmsResponse
                {
                    Items = items,
                    Truncated = truncated,
                    LoadedAtUtc = DateTimeOffset.UtcNow
                };
                _version++;
                _refreshError = null;
                _nextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogInformation("Active alarms refreshed. Count={Count}, Truncated={Truncated}, DurationMs={DurationMs}", items.Count, truncated, watch.ElapsedMilliseconds);
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