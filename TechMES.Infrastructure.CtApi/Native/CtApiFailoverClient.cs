using System.Globalization;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Scada;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi.Native;

/// <summary>Два независимых CtApi-подключения. Недоступный резерв не блокирует рабочий сервер.</summary>
public sealed class CtApiFailoverClient : ICtApiNativeClient, IAsyncDisposable
{
    private readonly ICtApiNativeClient[] _clients;
    private readonly bool[] _opened = new bool[2];
    private readonly bool?[] _lastSent = new bool?[2];
    private readonly SemaphoreSlim[] _serverGates = [new(1, 1), new(1, 1)];
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CtApiOptions _options;
    private readonly ILogger _logger;

    private Task[] _cycles = [Task.CompletedTask, Task.CompletedTask];
    private CancellationTokenSource? _stop = new();
    private TaskCompletionSource _ready = NewSignal();
    private bool _closing;
    private bool _disposed;
    private PlantScadaRedundancyState _state;

    public PlantScadaRedundancyState State => Volatile.Read(ref _state);

    public CtApiFailoverClient(CtApiOptions options, ICtApiNativeClient primary, ICtApiNativeClient secondary, ILogger<CtApiFailoverClient> logger)
    {
        _options = options;
        _clients = [primary, secondary];
        _logger = logger;

        _state = new()
        {
            HealthPeriodSeconds = Math.Max(5, options.HealthCheckPeriodSeconds),

            Primary = new()
            {
                Role = "Primary",
                Server = options.Server.Trim(),
                Configured = true,
                WriteTag = options.PrimaryConnectionTag.Trim(),
                ReadTag = options.PrimaryStatusTag.Trim()
            },

            Secondary = new()
            {
                Role = "Secondary",
                Server = options.ServerSecondary.Trim(),
                Configured = !string.IsNullOrWhiteSpace(options.ServerSecondary),
                WriteTag = options.SecondaryConnectionTag.Trim(),
                ReadTag = options.SecondaryStatusTag.Trim()
            }
        };
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int ActiveIndex(PlantScadaRedundancyState state) => state.Primary.Connected ? 0 : state.Secondary.Connected ? 1 : -1;

    /// <summary>Запускает максимум одну проверку каждого сервера и ждёт только первого рабочего подключения.</summary>
    public async Task RefreshAsync(bool writeControlTags, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task ready;
        Task completed;

        lock (_stateGate)
        {
            ThrowIfUnavailable();
            _stop ??= new CancellationTokenSource();

            for (var index = 0; index < _clients.Length; index++)
            {
                if (!_cycles[index].IsCompleted)
                    continue;

                var serverIndex = index;
                var token = _stop.Token;
                _cycles[index] = Task.Run(() => RefreshServerAsync(serverIndex, writeControlTags, token));
            }

            ready = _ready.Task;

            if (ready.IsCompleted)
                return;

            completed = Task.WhenAll(_cycles);
        }

        // Второй сервер продолжает свою проверку в отслеживаемой задаче.
        // Отмена ожидания не закрывает handle, которым пользуются другие запросы.
        await Task.WhenAny(ready, completed).WaitAsync(ct);
    }

    private async Task RefreshServerAsync(int index, bool writeControlTags, CancellationToken ct)
    {
        try
        {
            await _serverGates[index].WaitAsync(ct);

            try
            {
                var state = index == 0 ? State.Primary : State.Secondary;
                var duplicate = index == 1 && state.Server.Length > 0 && state.Server.Equals(State.Primary.Server, StringComparison.OrdinalIgnoreCase);

                state = duplicate
                    ? state with { Connected = false, Message = "Primary and Secondary must be different servers." }
                    : await ProbeAsync(index, state, ct);

                // Рабочий маршрут публикуем сразу после health-check, до heartbeat и чтения статусов.
                PublishServer(index, state);
                state = await UpdateHeartbeatAsync(index, state, writeControlTags, ct);
                PublishServer(index, state);
            }
            finally
            {
                _serverGates[index].Release();
            }

            // Оба ответа PLC читаем через любой рабочий сервер, даже если второй отключён.
            await RefreshPlcStatusesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CtApi server cycle failed. Index={Index}.", index);
        }
    }

    private void PublishServer(int index, PlantScadaServerState server)
    {
        string? previous;
        string? active;

        lock (_stateGate)
        {
            var state = State;
            var current = index == 0 ? state.Primary : state.Secondary;

            // Проверка связи не должна затирать более свежий ответ PLC.
            server = server with { ReadValue = current.ReadValue, ReadOk = current.ReadOk, ReadMessage = current.ReadMessage, ReadAtUtc = current.ReadAtUtc };
            state = index == 0 ? state with { Primary = server } : state with { Secondary = server };

            previous = State.ActiveRole;
            var next = ActiveIndex(state);
            active = next == 0 ? "Primary" : next == 1 ? "Secondary" : null;

            if (next < 0)
            {
                // Без рабочего маршрута прежний ответ PLC нельзя выдавать за актуальный.
                state = state with { Primary = ClearPlcStatus(state.Primary), Secondary = ClearPlcStatus(state.Secondary) };
            }

            Volatile.Write(ref _state, state with
            {
                ActiveRole = active,
                ActiveServer = next == 0 ? state.Primary.Server : next == 1 ? state.Secondary.Server : null,
                CheckedAtUtc = DateTimeOffset.UtcNow
            });

            if (next >= 0)
                _ready.TrySetResult();
            else if (_ready.Task.IsCompleted)
                _ready = NewSignal();
        }

        if (previous != active)
            _logger.LogInformation("CtApi active server changed: {Previous} -> {Current}.", previous, active);
    }

    /// <summary>Состояние соединения определяет только HealthCheckTag собственного сервера.</summary>
    private async Task<PlantScadaServerState> ProbeAsync(int index, PlantScadaServerState state, CancellationToken ct)
    {
        if (!state.Configured)
            return state with { Connected = false, Message = "Not configured." };

        try
        {
            ct.ThrowIfCancellationRequested();

            if (!_opened[index])
            {
                await _clients[index].OpenAsync(ct);
                _opened[index] = true;
            }

            var connected = await _clients[index].TryProbeConnectionAsync(ct);
            ct.ThrowIfCancellationRequested();

            var details = _clients[index] is CtApiNativeClient native ? native.LastProbeMessage : "CtApi health probe completed.";

            if (!connected)
                throw new InvalidOperationException("CtApi health probe failed. " + details);

            return state with { Connected = true, Message = "CtApi health probe succeeded. " + details };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("CtApi {Role} health check failed. Server={Server}. {Reason}", state.Role, state.Server, ex.Message);

            state = state with { Connected = false, Message = ex.Message, WriteOk = null };
            PublishServer(index, state);
            await CloseClientAsync(index);

            return state;
        }
    }

    private async Task RefreshPlcStatusesAsync(CancellationToken ct)
    {
        if (State.ActiveRole is null || !await _statusGate.WaitAsync(0, ct))
            return;

        try
        {
            for (var index = 0; index < _clients.Length; index++)
            {
                var result = await ReadPlcStatusAsync(index == 0 ? State.Primary : State.Secondary, ct);

                lock (_stateGate)
                {
                    var state = State;
                    var current = index == 0 ? state.Primary : state.Secondary;
                    var updated = state.ActiveRole is null ? ClearPlcStatus(current) : current with { ReadValue = result.ReadValue, ReadOk = result.ReadOk, ReadMessage = result.ReadMessage, ReadAtUtc = result.ReadAtUtc };
                    Volatile.Write(ref _state, index == 0 ? state with { Primary = updated } : state with { Secondary = updated });
                }
            }
        }
        finally
        {
            _statusGate.Release();
        }
    }

    private static PlantScadaServerState ClearPlcStatus(PlantScadaServerState state) => state with { ReadValue = null, ReadOk = null, ReadAtUtc = null, ReadMessage = "No connection. PLC status is unavailable." };

    private bool IsDuplicateTag(string tag)
    {
        return tag.Length > 0 && new[]
            {
                State.Primary.WriteTag,
                State.Primary.ReadTag,
                State.Secondary.WriteTag,
                State.Secondary.ReadTag
            }.Count(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    private async Task<PlantScadaServerState> UpdateHeartbeatAsync(int index, PlantScadaServerState state, bool write, CancellationToken ct)
    {
        state = state with
        {
            WriteValue = null,
            WriteTagReadOk = null,
            WriteOk = null
        };

        if (!state.Connected || !state.Configured)
        {
            _lastSent[index] = null;
            return state with { WriteMessage = "Server is offline. Heartbeat is not written." };
        }

        if (state.WriteTag.Length == 0)
        {
            return state with
            {
                WriteMessage = "Write tag is not configured."
            };
        }

        if (IsDuplicateTag(state.WriteTag))
        {
            return state with
            {
                WriteTagReadOk = false,
                WriteMessage = "All four tag names must be different."
            };
        }

        try
        {
            // Heartbeat сервера записывается через его собственный handle.
            var client = _clients[index];
            var value = await client.ReadControlTagAsync(state.WriteTag, ct);

            state = state with { WriteValue = value };

            if (!TryBoolean(value, out var current))
            {
                return state with
                {
                    WriteTagReadOk = false,
                    WriteMessage = "Expected a readable boolean Write tag (0/1)."
                };
            }

            state = state with { WriteTagReadOk = true };

            if (!write)
            {
                return state with
                {
                    WriteMessage = "Readable. Waiting for the health cycle."
                };
            }

            if (!_options.AllowWrites)
            {
                _lastSent[index] = null;

                return state with
                {
                    WriteMessage = "Writes disabled by CtApi:AllowWrites."
                };
            }

            // При первом запуске инвертируем фактическое значение.
            // Далее инвертируем последнюю успешную запись:
            // задержка readback не должна повторять один и тот же бит.
            var next = !(_lastSent[index] ?? current);
            var sent = next ? "1" : "0";

            await client.WriteControlTagAsync(state.WriteTag, sent, ct);

            _lastSent[index] = next;

            return state with
            {
                LastWrittenValue = sent,
                WriteOk = true,
                WriteMessage = $"Heartbeat write accepted: {sent}."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // После неподтверждённой записи следующий цикл
            // снова синхронизируется с фактическим значением.
            _lastSent[index] = null;

            return state with
            {
                WriteTagReadOk = state.WriteTagReadOk ?? false,
                WriteOk = state.WriteTagReadOk == true ? false : null,
                WriteMessage = ex.Message
            };
        }
    }

    private async Task<PlantScadaServerState> ReadPlcStatusAsync(PlantScadaServerState state, CancellationToken ct)
    {
        state = state with
        {
            ReadValue = null,
            ReadOk = null,
            ReadAtUtc = null
        };

        if (state.ReadTag.Length == 0)
        {
            return state with
            {
                ReadMessage = "Read status tag is not configured."
            };
        }

        if (IsDuplicateTag(state.ReadTag))
        {
            return state with
            {
                ReadOk = false,
                ReadMessage = "All four tag names must be different."
            };
        }

        if (State.ActiveRole is null)
        {
            return state with
            {
                ReadMessage = "No connection. PLC status is unavailable."
            };
        }

        try
        {
            var value = await UseAsync(client => client.ReadControlTagAsync(state.ReadTag, ct), ct);
            var valid = TryBoolean(value, out _);

            return state with
            {
                ReadValue = value,
                ReadOk = valid,
                ReadAtUtc = DateTimeOffset.UtcNow,
                ReadMessage = valid ? "PLC status read successfully." : "Expected a boolean PLC status (0/1)."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return state with
            {
                ReadOk = false,
                ReadMessage = ex.Message
            };
        }
    }

    private static bool TryBoolean(string? value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && (number == 0 || number == 1))
        {
            result = number == 1;
            return true;
        }

        result = false;
        return false;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_closing)
            throw new InvalidOperationException("CtApi connections are closing.");
    }

    private async Task<T> UseAsync<T>(Func<ICtApiNativeClient, Task<T>> operation, CancellationToken ct)
    {
        while (true)
        {
            int index;

            lock (_stateGate)
            {
                ThrowIfUnavailable();
                index = ActiveIndex(State);
            }

            if (index < 0)
                throw new InvalidOperationException("Neither CtApi server is connected.");

            // Ожидание недоступного резерва не занимает gate рабочего сервера.
            await _serverGates[index].WaitAsync(ct);

            try
            {
                lock (_stateGate)
                {
                    ThrowIfUnavailable();

                    if (index != ActiveIndex(State))
                        continue;
                }

                // Уже отправленную операцию автоматически не повторяем на другом сервере.
                return await operation(_clients[index]);
            }
            finally
            {
                _serverGates[index].Release();
            }
        }
    }

    public Task OpenAsync(CancellationToken ct = default) => RefreshAsync(false, ct);

    public Task<bool> TryProbeConnectionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(State.ActiveRole is not null);
    }

    public Task<string?> TagReadAsync(string tagName, CancellationToken ct = default) => UseAsync(client => client.TagReadAsync(tagName, ct), ct);

    public Task<string?> ReadControlTagAsync(string tagName, CancellationToken ct = default) => UseAsync(client => client.ReadControlTagAsync(tagName, ct), ct);

    public async Task WriteControlTagAsync(string tagName, string value, CancellationToken ct = default) => await UseAsync(async client => { await client.WriteControlTagAsync(tagName, value, ct); return true; }, ct);

    public Task<string?> CicodeAsync(string command, CancellationToken ct = default) => UseAsync(client => client.CicodeAsync(command, ct), ct);

    public Task<IReadOnlyList<Dictionary<string, string>>> FindAsync(string tableName, string? filter, string? cluster, IReadOnlyList<string> properties, CancellationToken ct = default) => UseAsync(client => client.FindAsync(tableName, filter, cluster, properties, ct), ct);

    public async Task TagWriteAsync(string tagName, string? value, CancellationToken ct = default) => await UseAsync(async client => { await client.TagWriteAsync(tagName, value, ct); return true; }, ct);

    private async Task CloseClientAsync(int index)
    {
        try
        {
            await _clients[index].CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CtApi close failed for server {Index}.", index);
        }
        finally
        {
            _opened[index] = false;
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _closeGate.WaitAsync(ct);

        try
        {
            CancellationTokenSource stop;
            Task completed;

            lock (_stateGate)
            {
                if (_stop is null)
                    return;

                _closing = true;
                stop = _stop;
                completed = Task.WhenAll(_cycles);
            }

            // Не закрываем handle, пока нативный вызов действительно не завершился.
            stop.Cancel();
            await completed;

            for (var index = 0; index < _clients.Length; index++)
            {
                await _serverGates[index].WaitAsync();

                try
                {
                    await CloseClientAsync(index);
                }
                finally
                {
                    _serverGates[index].Release();
                }
            }

            lock (_stateGate)
            {
                Array.Clear(_lastSent);
                Volatile.Write(ref _state, State with { ActiveRole = null, ActiveServer = null, Primary = ClearPlcStatus(State.Primary with { Connected = false }), Secondary = ClearPlcStatus(State.Secondary with { Connected = false }) });
                _cycles = [Task.CompletedTask, Task.CompletedTask];
                _ready = NewSignal();
                _stop = null;
                _closing = false;
            }

            stop.Dispose();
        }
        finally
        {
            _closeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        await CloseAsync();

        foreach (var client in _clients)
        {
            if (client is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}