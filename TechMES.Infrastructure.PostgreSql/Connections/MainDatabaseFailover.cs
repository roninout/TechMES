using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TechMES.Contracts.Runtime;

namespace TechMES.Infrastructure.PostgreSql.Connections;

/// <summary>
/// Общий маршрут Main DB. Проверки запускаются тем же health worker, что CtApi/ODBC.
/// Переключается только открытие соединения. SQL store-ов и транзакции автоматически не повторяются.
/// </summary>
public sealed class MainDatabaseFailover : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string[] _connections;
    private readonly bool[] _online = new bool[2];
    private readonly bool[] _readOnly = new bool[2];
    private readonly long[] _versions = new long[2];
    private readonly DateTimeOffset?[] _checkedAtUtc = new DateTimeOffset?[2];
    private readonly Task[] _checks = [Task.CompletedTask, Task.CompletedTask];
    private readonly CancellationTokenSource _stop = new();
    private readonly IMainDatabaseConnector _connector;
    private readonly ILogger<MainDatabaseFailover> _logger;
    private readonly int _healthSeconds;
    private readonly bool _enabled;

    private TaskCompletionSource _changed = NewSignal();
    private bool _started;
    private bool _disposed;

    public MainDatabaseFailover(IConfiguration configuration, IMainDatabaseConnector connector, ILogger<MainDatabaseFailover> logger)
    {
        _connections = [(configuration.GetConnectionString("Default") ?? configuration["Database:ConnectionString"])?.Trim() ?? "", configuration["Database:ConnectionStringSecondary"]?.Trim() ?? ""];
        _healthSeconds = int.TryParse(configuration["CtApi:HealthCheckPeriodSeconds"], out var seconds) ? Math.Max(5, seconds) : 10;
        _enabled = string.Equals(configuration["Database:Provider"] ?? "PostgreSql", "PostgreSql", StringComparison.OrdinalIgnoreCase);
        _connector = connector;
        _logger = logger;
    }

    // Используются bootstrapper-ом внутри PostgreSQL-адаптера, в HTTP-ответ не попадают.
    internal string PrimaryConnectionString => _connections[0];
    internal bool HasSecondary => _connections[1].Length > 0;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Role(int index) => index == 0 ? "Primary" : "Secondary";

    private int ActiveIndex() => _online[0] ? 0 : _online[1] ? 1 : -1;

    /// <summary>
    /// Без I/O возвращает согласованный снимок двух серверов.
    /// </summary>
    public MainDatabaseHealthResponse GetHealth()
    {
        lock (_gate)
        {
            MainDatabaseServerState DescribeServer(int index) => new()
            {
                Role = Role(index),
                Configured = _connections[index].Length > 0,
                Connected = _online[index] || _readOnly[index],
                CanWrite = _online[index],
                CheckedAtUtc = _checkedAtUtc[index],
                Message = _connections[index].Length == 0 ? "Not configured." : _checkedAtUtc[index] is null ? "Waiting for the first Main DB health check." : _readOnly[index] ? "Database is reachable but read-only; it is not selected for Main DB writes." : _online[index] ? "Main DB connection and read-write check succeeded." : "Main DB connection is unavailable. See Runtime logs."
            };

            var active = ActiveIndex();

            return new MainDatabaseHealthResponse
            {
                Enabled = _enabled,
                HealthPeriodSeconds = _healthSeconds,
                ActiveRole = active < 0 ? null : Role(active),
                Primary = DescribeServer(0),
                Secondary = DescribeServer(1)
            };
        }
    }

    /// <summary>
    /// Пустой Secondary не опрашивается.
    /// По каждому серверу одновременно выполняется максимум одна проверка.
    /// </summary>
    public void RefreshHealth()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _started = true;

            if (!_enabled)
                return;

            for (var index = 0; index < _connections.Length; index++)
            {
                if (_connections[index].Length == 0 || !_checks[index].IsCompleted)
                    continue;

                var serverIndex = index;
                var version = ++_versions[index];

                _checks[index] = Task.Run(() => ProbeAsync(serverIndex, version, _stop.Token));
            }
        }
    }

    private async Task ProbeAsync(int index, long version, CancellationToken ct)
    {
        try
        {
            await using var connection = await _connector.OpenAsync(_connections[index], _healthSeconds, ct);

            ct.ThrowIfCancellationRequested();

            Publish(index, version, true, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && Publish(index, version, false, ex is MainDatabaseReadOnlyException))
                _logger.LogWarning("Main DB {Role} health check failed. ErrorType={ErrorType}.", Role(index), ex.GetType().Name);
        }
    }

    /// <summary>
    /// Публикует результат проверки и будит ожидающие открытия соединения.
    /// Более старый результат не затирает новое состояние.
    /// </summary>
    private bool Publish(int index, long version, bool online, bool readOnly)
    {
        lock (_gate)
        {
            if (_disposed || version != _versions[index])
                return false;

            var previous = ActiveIndex();

            _online[index] = online;
            _readOnly[index] = readOnly;
            _checkedAtUtc[index] = DateTimeOffset.UtcNow;

            var active = ActiveIndex();
            var changed = _changed;

            _changed = NewSignal();
            changed.TrySetResult();

            if (previous != active)
                _logger.LogInformation("Main DB active server changed: {Previous} -> {Current}.", previous < 0 ? "None" : Role(previous), active < 0 ? "None" : Role(active));

            return true;
        }
    }

    /// <summary>
    /// Выбирает доступный сервер с приоритетом Primary.
    /// Медленная проверка одного сервера не задерживает рабочий второй.
    /// </summary>
    private async Task<int> SelectAsync(HashSet<int> attempted, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            Task pending;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                for (var index = 0; index < _online.Length; index++)
                {
                    if (_online[index] && !attempted.Contains(index))
                        return index;
                }

                var checks = _checks.Where((task, index) => !attempted.Contains(index) && !task.IsCompleted).ToArray();

                if (checks.Length == 0)
                    return -1;

                changed = _changed.Task;
                pending = Task.WhenAll(checks);
            }

            await Task.WhenAny(changed, pending).WaitAsync(ct);
        }
    }

    /// <summary>
    /// Возвращает проверенное открытое соединение.
    /// Повтор разрешён только до передачи соединения store-у.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_started)
                RefreshHealth();
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        var token = stop.Token;
        var attempted = new HashSet<int>();

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var index = await SelectAsync(attempted, token);

            if (index < 0)
                throw new InvalidOperationException("Neither Main DB server is available for writing. Check Primary/Secondary settings and Runtime logs.");

            attempted.Add(index);

            long version;

            lock (_gate)
                version = _versions[index];

            try
            {
                var connection = await _connector.OpenAsync(_connections[index], _healthSeconds, token);

                if (token.IsCancellationRequested)
                {
                    await connection.DisposeAsync();
                    token.ThrowIfCancellationRequested();
                }

                Publish(index, version, true, false);

                return connection;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (Publish(index, version, false, ex is MainDatabaseReadOnlyException))
                        _versions[index]++;
                }

                _logger.LogWarning("Main DB {Role} open failed. Trying another available server. ErrorType={ErrorType}.", Role(index), ex.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _changed.TrySetResult();

            pending = Task.WhenAll(_checks);
        }

        _stop.Cancel();

        await pending;

        _stop.Dispose();
    }
}