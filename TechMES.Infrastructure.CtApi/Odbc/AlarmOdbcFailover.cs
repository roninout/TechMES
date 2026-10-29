using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TechMES.Infrastructure.CtApi.Odbc;

/// <summary>
/// Общий выбор ODBC для аварий и журнала событий. Primary всегда имеет приоритет.
/// Проверки запускает существующий health worker; недоступный сервер не блокирует второй.
/// </summary>
public sealed class AlarmOdbcFailover : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string[] _connections;
    private readonly bool[] _online = new bool[2];
    private readonly long[] _versions = new long[2];
    private readonly Task[] _checks = [Task.CompletedTask, Task.CompletedTask];
    private readonly CancellationTokenSource _stop = new();
    private readonly IAlarmOdbcConnectionFactory _factory;
    private readonly ILogger<AlarmOdbcFailover> _logger;
    private readonly int _healthSeconds;

    private TaskCompletionSource _changed = NewSignal();
    private bool _started;
    private bool _disposed;

    public AlarmOdbcFailover(IConfiguration configuration, IAlarmOdbcConnectionFactory factory, ILogger<AlarmOdbcFailover> logger)
    {
        // Прежнее имя Primary оставляем: существующая строка подключения продолжит работать.
        _connections = [configuration["CtApi:AlarmOdbcConnectionString"]?.Trim() ?? "", configuration["CtApi:AlarmOdbcConnectionStringSecondary"]?.Trim() ?? ""];
        _healthSeconds = int.TryParse(configuration["CtApi:HealthCheckPeriodSeconds"], out var seconds) ? Math.Max(5, seconds) : 10;
        _factory = factory;
        _logger = logger;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Role(int index) => index == 0 ? "Primary" : "Secondary";

    private int ActiveIndex() => _online[0] ? 0 : _online[1] ? 1 : -1;

    /// <summary>
    /// Запускает не более одной фоновой проверки на сервер.
    /// Сразу возвращает управление существующему health worker.
    /// </summary>
    public void RefreshHealth()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _started = true;

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

    /// <summary>
    /// Проверяет открытие подключения и выполнение короткого запроса.
    /// Список аварий во время health-check не загружается.
    /// </summary>
    private async Task ProbeAsync(int index, long version, CancellationToken ct)
    {
        try
        {
            using var connection = _factory.Create(_connections[index], _healthSeconds);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT FullName FROM CiAdvancedAlarm WHERE 1 = 0";
            command.CommandTimeout = _healthSeconds;

            using var reader = await command.ExecuteReaderAsync(ct);
            ct.ThrowIfCancellationRequested();

            Publish(index, version, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Строки подключения и сообщения драйвера могут содержать пароль: в журнал их не выводим.
            if (!ct.IsCancellationRequested && Publish(index, version, false))
                _logger.LogWarning("ODBC {Role} health check failed. ErrorType={ErrorType}.", Role(index), ex.GetType().Name);
        }
    }

    /// <summary>
    /// Публикует состояние и автоматически выбирает Primary, если он доступен.
    /// Старая проверка не может восстановить маршрут после более свежей ошибки чтения.
    /// </summary>
    private bool Publish(int index, long version, bool online)
    {
        lock (_gate)
        {
            if (_disposed || version != _versions[index])
                return false;

            var previous = ActiveIndex();

            _online[index] = online;

            var active = ActiveIndex();
            var changed = _changed;

            _changed = NewSignal();
            changed.TrySetResult();

            if (previous != active)
                _logger.LogInformation("ODBC active server changed: {Previous} -> {Current}.", previous < 0 ? "None" : Role(previous), active < 0 ? "None" : Role(active));

            return true;
        }
    }

    /// <summary>
    /// Ожидает только пригодный маршрут.
    /// Медленная проверка резерва не задерживает рабочий Primary, и наоборот.
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
    /// Выполняет полностью один SELECT-сценарий на одном сервере.
    /// При ошибке связи повторяет весь сценарий на другом доступном сервере.
    /// Частичные результаты разных серверов не смешиваются.
    /// </summary>
    public async Task<T> ReadAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
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
                throw new InvalidOperationException("Neither ODBC server is available. Check Primary/Secondary settings and Runtime logs.");

            attempted.Add(index);

            long version;

            lock (_gate)
                version = _versions[index];

            var opened = false;

            try
            {
                // System.Data.Odbc может выполнять Async-методы синхронно внутри драйвера.
                // Каждый сценарий работает на отдельном подключении в фоновой задаче.
                return await Task.Run(async () =>
                {
                    using var connection = _factory.Create(_connections[index], _healthSeconds);
                    await connection.OpenAsync(token);

                    opened = true;

                    var result = await read(connection, token);

                    token.ThrowIfCancellationRequested();

                    return result;
                }, token);
            }
            catch (DbException ex) when (!token.IsCancellationRequested && (!opened || IsConnectionFailure(ex)))
            {
                lock (_gate)
                {
                    if (version == _versions[index])
                    {
                        Publish(index, version, false);
                        _versions[index]++;
                    }
                }

                _logger.LogWarning("ODBC {Role} read connection failed. Trying another available server. ErrorType={ErrorType}.", Role(index), ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Ошибка SQL или схемы не переключает сервер.
    /// Ошибки связи и таймауты допускают повтор SELECT на другом сервере.
    /// </summary>
    private static bool IsConnectionFailure(DbException exception)
    {
        return exception.IsTransient || exception is OdbcException odbc && odbc.Errors.Cast<OdbcError>().Any(error => error.SQLState.StartsWith("08", StringComparison.Ordinal) || error.SQLState is "HYT00" or "HYT01");
    }

    /// <summary>
    /// Отмена не закрывает подключение из другого потока.
    /// Каждый драйверный вызов завершает и освобождает свой using.
    /// </summary>
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