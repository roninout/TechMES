using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Хранит последний успешный снимок аварий. HTTP-запросы WEB читают память,
/// а продолжительный опрос CtApi выполняется отдельно от HTTP-запроса.
/// </summary>
public sealed class CtApiActiveAlarmProvider : IActiveAlarmProvider
{
    private const int MaxRows = 1000;
    private static readonly string[] Properties = ["TAG", "NAME", "DESC", "AREA", "CATEGORY", "STATE", "ONDATE", "OFFDATE", "ACKDATE"];

    private readonly ICtApiNativeClient _client;
    private readonly ILogger<CtApiActiveAlarmProvider> _logger;
    private readonly int _area;
    private readonly object _stateLock = new();

    private ActiveAlarmsResponse? _snapshot;
    private Task? _refreshTask;
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
    /// Немедленно возвращает последний успешный снимок. При необходимости запускает
    /// один фоновый опрос CtApi для всех открытых WEB-клиентов.
    /// </summary>
    public Task<ActiveAlarmsResponse> GetActiveAsync(bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;

            if ((_refreshTask is null || _refreshTask.IsCompleted) && (forceRefresh || now >= _nextRefreshAt))
            {
                _nextRefreshAt = now.AddMinutes(1);
                _refreshTask = Task.Run(RefreshCoreAsync);
            }

            var notModified = _snapshot is not null && knownVersion == _version;

            return Task.FromResult(new ActiveAlarmsResponse
            {
                Items = notModified ? Array.Empty<ActiveAlarmDto>() : (_snapshot?.Items ?? Array.Empty<ActiveAlarmDto>()),
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
    /// Выполняет полный опрос вне HTTP-запроса. При ошибке сохраняет прежний снимок
    /// и назначает повторную попытку; после успеха атомарно публикует новый список.
    /// </summary>
    private async Task RefreshCoreAsync()
    {
        var watch = Stopwatch.StartNew();

        try
        {
            // Сохраняем запрос из текущей ветки. Проверку STATE/OFFDATE уточним сравнением конкретных аварий с интерфейсом Plant SCADA.
            var query = $"CTAPIAlarm(0,0,{_area})";
            var (rows, truncated) = await _client.FindAlarmsAsync(query, MaxRows, Properties);

            if (rows.Any(row => string.IsNullOrWhiteSpace(Get(row, "TAG")) && string.IsNullOrWhiteSpace(Get(row, "NAME"))))
                throw new InvalidOperationException("CtApi returned an alarm without TAG/NAME. Verify alarm property names.");

            var items = rows.Where(IsOn).Select(ToAlarm).ToList();

            var snapshot = new ActiveAlarmsResponse
            {
                Items = items,
                Truncated = truncated,
                LoadedAtUtc = DateTimeOffset.UtcNow
            };

            lock (_stateLock)
            {
                _snapshot = snapshot;
                _version++;
                _refreshError = null;
                _nextRefreshAt = DateTimeOffset.UtcNow.AddMinutes(1);
            }

            _logger.LogInformation("Active alarms refreshed. Read={ReadCount}, Active={ActiveCount}, Truncated={Truncated}, DurationMs={DurationMs}", rows.Count, items.Count, truncated, watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _refreshError = _snapshot is null
                    ? "The alarm list could not be loaded. Runtime will retry."
                    : "The alarm list could not be refreshed. Showing the last successful snapshot.";

                _nextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            _logger.LogError(ex, "Active alarm refresh failed after {DurationMs} ms.", watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Исключает снятые, но ещё не квитированные аварии по существующему правилу.
    /// </summary>
    private static bool IsOn(IReadOnlyDictionary<string, string> row)
    {
        if (!string.IsNullOrWhiteSpace(Get(row, "OFFDATE")))
            return false;

        return !Get(row, "STATE").Equals("OFF", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает поле CtApi или пустую строку, если поле недоступно.
    /// </summary>
    private static string Get(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : "";
    }

    /// <summary>
    /// Преобразует строку в DTO и оставляет исходные значения для диагностики.
    /// </summary>
    private static ActiveAlarmDto ToAlarm(Dictionary<string, string> row)
    {
        return new ActiveAlarmDto
        {
            Tag = string.IsNullOrWhiteSpace(Get(row, "TAG")) ? Get(row, "NAME") : Get(row, "TAG"),
            Description = Get(row, "DESC"),
            Area = Get(row, "AREA"),
            Category = Get(row, "CATEGORY"),
            State = Get(row, "STATE"),
            OnDate = Get(row, "ONDATE"),
            AckDate = Get(row, "ACKDATE"),
            RawFields = row
        };
    }
}