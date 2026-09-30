using Microsoft.Extensions.Configuration;
using TechMES.Application.Alarms;
using TechMES.Contracts.Alarms;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Читает сводку аварий через существующий CtApi failover client.
/// Один снимок используется всеми открытыми вкладками WEB в течение 30 секунд.
/// </summary>
public sealed class CtApiActiveAlarmProvider : IActiveAlarmProvider
{
    private const int MaxRows = 250;

    private static readonly string[] Properties =
    [
        "TAG", "NAME", "DESC", "AREA", "CATEGORY",
        "STATE", "ONDATE", "OFFDATE", "ACKDATE"
    ];

    private readonly ICtApiNativeClient _client;
    private readonly int _area;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private ActiveAlarmsResponse? _snapshot;
    private DateTimeOffset _expiresAt;

    public CtApiActiveAlarmProvider(ICtApiNativeClient client, IConfiguration configuration)
    {
        _client = client;
        _area = configuration.GetValue("CtApi:AlarmArea", -1);
    }

    /// <summary>
    /// Возвращает свежий снимок. SemaphoreSlim предотвращает одновременное
    /// выполнение одинакового тяжёлого запроса от нескольких WEB-клиентов.
    /// </summary>
    public async Task<ActiveAlarmsResponse> GetActiveAsync(CancellationToken ct = default)
    {
        if (_snapshot is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _snapshot;

        await _refreshGate.WaitAsync(ct);

        try
        {
            if (_snapshot is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _snapshot;

            // Type=0: сводка включает ON, а также ещё не квитированные OFF.
            // Area=-1: текущая область CtApi-пользователя.
            var query = $"CTAPIAlarm(0,0,{_area})";
            var (rows, truncated) = await _client.FindAlarmsAsync(query, MaxRows, Properties, ct);

            if (rows.Any(row => string.IsNullOrWhiteSpace(Get(row, "TAG")) &&
                                string.IsNullOrWhiteSpace(Get(row, "NAME"))))
            {
                throw new InvalidOperationException(
                    "Plant SCADA returned an alarm without TAG/NAME. Verify CtApi property names.");
            }

            var items = rows.Where(IsOn).Select(ToAlarm).ToList();

            _snapshot = new ActiveAlarmsResponse
            {
                Items = items,
                Truncated = truncated,
                LoadedAtUtc = DateTimeOffset.UtcNow
            };

            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
            return _snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Исключает снятую, но ещё не квитированную аварию.
    /// Имена и формат STATE/OFFDATE нужно сверить на реальном CtApi.
    /// </summary>
    private static bool IsOn(IReadOnlyDictionary<string, string> row)
    {
        if (row.TryGetValue("OFFDATE", out var offDate) &&
            !string.IsNullOrWhiteSpace(offDate))
        {
            return false;
        }

        return !row.TryGetValue("STATE", out var state) ||
               !state.Equals("OFF", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Получает значение поля, которое может отсутствовать в конкретной версии CtApi.
    /// </summary>
    private static string Get(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : "";
    }

    /// <summary>
    /// Преобразует прочитанную строку, сохраняя исходные поля для диагностики.
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