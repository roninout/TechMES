using System.Globalization;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Scada;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi.Native;

/// <summary>
/// Два независимых CtApi handle и один маршрут для всех потребителей.
/// Gate не позволяет менять активный handle посреди операции или записи статусов.
/// </summary>
public sealed class CtApiFailoverClient : ICtApiNativeClient, IAsyncDisposable
{
    private readonly ICtApiNativeClient[] _clients;
    private readonly bool[] _opened = new bool[2];
    private readonly bool?[] _lastSent = new bool?[2];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CtApiOptions _options;
    private readonly ILogger _logger;

    private int _active = -1;
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

    public async Task RefreshAsync(bool writeControlTags, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var primary = await ProbeAsync(0, State.Primary, ct);
            var duplicate = primary.Server.Length > 0 && string.Equals(primary.Server, State.Secondary.Server, StringComparison.OrdinalIgnoreCase);
            var secondary = duplicate 
                ? State.Secondary with
                {
                    Connected = false,
                    Message = "Primary and Secondary must be different servers."
                }
                : await ProbeAsync(1, State.Secondary, ct);

            var next = primary.Connected ? 0 : secondary.Connected ? 1 : -1;

            if (_active != next)
                _logger.LogInformation("CtApi active server changed: {Previous} -> {Current}.", _active, next);

            _active = next;

            // Health API читает готовый снимок и не ждёт нативные операции.
            // Для ответа PLC сохраняется собственное время чтения ReadAtUtc.
            Volatile.Write(ref _state, State with
            {
                Primary = primary with
                {
                    WriteOk = null,
                    WriteMessage = "Health cycle in progress."
                },
                Secondary = secondary with
                {
                    WriteOk = null,
                    WriteMessage = "Health cycle in progress."
                },
                CheckedAtUtc = DateTimeOffset.UtcNow,
                ActiveRole = next == 0
                    ? "Primary"
                    : next == 1
                        ? "Secondary"
                        : null,
                ActiveServer = next == 0
                    ? primary.Server
                    : next == 1
                        ? secondary.Server
                        : null
            });

            primary = await UpdateHeartbeatAsync(0, primary, writeControlTags, ct);
            secondary = await UpdateHeartbeatAsync(1, secondary, writeControlTags, ct);

            // Общие теги читаем через выбранное рабочее соединение.
            // Ошибка heartbeat не отменяет чтение ответа PLC.
            primary = await ReadPlcStatusAsync(primary, ct);
            secondary = await ReadPlcStatusAsync(secondary, ct);

            Volatile.Write(ref _state, State with
            {
                Primary = primary,
                Secondary = secondary
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Проверяет HealthCheckTag через собственное соединение указанного сервера.
    /// Read/Write-теги PLC не участвуют в определении Connected.
    /// Ошибка проверки закрывает соединение; следующий цикл попробует открыть его снова.
    /// </summary>
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

            // Каждый сервер проверяется через собственный CtApi-клиент.
            var connected = await _clients[index].TryProbeConnectionAsync(ct);

            // Передаём подробности в существующий снимок Diagnostics.
            var details = _clients[index] is CtApiNativeClient native ? native.LastProbeMessage : "CtApi health probe completed.";

            if (!connected)
                throw new InvalidOperationException("CtApi health probe failed. " + details);

            return state with
            {
                Connected = true,
                Message = "CtApi health probe succeeded. " + details
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("CtApi {Role} health check failed. Server={Server}. {Reason}", state.Role, state.Server, ex.Message);

            await CloseClientAsync(index);

            return state with
            {
                Connected = false,
                Message = ex.Message
            };
        }
    }

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
            _lastSent[index] = null;

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

        if (_active < 0)
        {
            _lastSent[index] = null;

            return state with
            {
                WriteMessage = "No connection. Heartbeat is not written."
            };
        }

        try
        {
            var client = _clients[_active];
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

            // Для отключённого сервера запрещена любая heartbeat-запись,
            // в том числе запись нуля.
            if (!state.Connected || !state.Configured)
            {
                _lastSent[index] = null;

                return state with
                {
                    WriteMessage = "Server is offline. Heartbeat is not written."
                };
            }

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

        if (_active < 0)
        {
            return state with
            {
                ReadMessage = "No connection. PLC status is unavailable."
            };
        }

        try
        {
            var value = await _clients[_active].ReadControlTagAsync(state.ReadTag, ct);
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

    private async Task<T> UseAsync<T>(Func<ICtApiNativeClient, Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_active < 0)
                throw new InvalidOperationException("Neither CtApi server is connected.");

            return await operation(_clients[_active]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task OpenAsync(CancellationToken ct = default) => RefreshAsync(false, ct);

    public Task<bool> TryProbeConnectionAsync(CancellationToken ct = default) => Task.FromResult(State.ActiveRole is not null);

    public Task<string?> TagReadAsync(string tagName, CancellationToken ct = default) => UseAsync(c => c.TagReadAsync(tagName, ct), ct);

    public Task<string?> ReadControlTagAsync(string tagName, CancellationToken ct = default) => UseAsync(c => c.ReadControlTagAsync(tagName, ct), ct);

    public async Task WriteControlTagAsync(string tagName, string value, CancellationToken ct = default) => await UseAsync(async c => { await c.WriteControlTagAsync(tagName, value, ct); return true; }, ct);

    public Task<string?> CicodeAsync(string command, CancellationToken ct = default) => UseAsync(c => c.CicodeAsync(command, ct), ct);

    public Task<IReadOnlyList<Dictionary<string, string>>> FindAsync(string tableName, string? filter, string? cluster, IReadOnlyList<string> properties, CancellationToken ct = default) => UseAsync(c => c.FindAsync(tableName, filter, cluster, properties, ct), ct);

    public async Task TagWriteAsync(string tagName, string? value, CancellationToken ct = default) => await UseAsync(async c => { await c.TagWriteAsync(tagName, value, ct); return true; }, ct);

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
        await _gate.WaitAsync(ct);

        try
        {
            await CloseClientAsync(0);
            await CloseClientAsync(1);

            _active = -1;
            Array.Clear(_lastSent);

            Volatile.Write(ref _state, State with { ActiveRole = null, ActiveServer = null, Primary = State.Primary with { Connected = false }, Secondary = State.Secondary with { Connected = false } });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await CloseAsync();
        _disposed = true;

        foreach (var client in _clients)
        {
            if (client is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}