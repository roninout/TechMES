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
            Primary = new() { Role = "Primary", Server = options.Server.Trim(), Configured = true, ControlTag = options.PrimaryConnectionTag.Trim() },
            Secondary = new() { Role = "Secondary", Server = options.ServerSecondary.Trim(), Configured = !string.IsNullOrWhiteSpace(options.ServerSecondary), ControlTag = options.SecondaryConnectionTag.Trim() }
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
            var secondary = duplicate ? State.Secondary with { Connected = false, Message = "Primary and Secondary must be different servers." } : await ProbeAsync(1, State.Secondary, ct);

            var next = primary.Connected ? 0 : secondary.Connected ? 1 : -1;

            if (_active != next)
                _logger.LogInformation("CtApi active server changed: {Previous} -> {Current}.", _active, next);

            _active = next;

            // Публикуем выбранный маршрут до записи тегов: health не ждёт нативную запись.
            Volatile.Write(ref _state, State with
            {
                Primary = primary with { TagReadOk = null, TagWriteOk = null, TagValue = null, TagMessage = "Checking control tag." },
                Secondary = secondary with { TagReadOk = null, TagWriteOk = null, TagValue = null, TagMessage = "Checking control tag." },
                CheckedAtUtc = DateTimeOffset.UtcNow,
                ActiveRole = next == 0 ? "Primary" : next == 1 ? "Secondary" : null,
                ActiveServer = next == 0 ? primary.Server : next == 1 ? secondary.Server : null
            });

            var duplicateTag = primary.ControlTag.Length > 0 && string.Equals(primary.ControlTag, secondary.ControlTag, StringComparison.OrdinalIgnoreCase);

            primary = await UpdateTagAsync(primary, writeControlTags, duplicateTag, ct);
            secondary = await UpdateTagAsync(secondary, writeControlTags, duplicateTag, ct);

            Volatile.Write(ref _state, State with { Primary = primary, Secondary = secondary });
        }
        finally
        {
            _gate.Release();
        }
    }

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

            // Используем прежний алгоритм CtApi health probe.
            if (!await _clients[index].TryProbeConnectionAsync(ct))
                throw new InvalidOperationException("CtApi health probe failed.");

            return state with { Connected = true, Message = "CtApi health probe succeeded." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await CloseClientAsync(index);
            return state with { Connected = false, Message = ex.Message };
        }
    }

    private async Task<PlantScadaServerState> UpdateTagAsync(PlantScadaServerState state, bool write, bool duplicate, CancellationToken ct)
    {
        state = state with { TagValue = null, TagReadOk = null, TagWriteOk = null };

        if (state.ControlTag.Length == 0)
            return state with { TagMessage = "Control tag is not configured." };

        if (duplicate)
            return state with { TagMessage = "Control tags must be different.", TagReadOk = false };

        if (_active < 0)
            return state with { TagMessage = "No connection. Control tag could not be updated." };

        try
        {
            var client = _clients[_active];
            var value = await client.ReadControlTagAsync(state.ControlTag, ct);

            state = state with { TagValue = value };

            if (!IsBoolean(value))
                return state with { TagReadOk = false, TagMessage = "Expected a readable boolean value (0/1)." };

            state = state with { TagReadOk = true };

            if (!write)
                return state with { TagMessage = "Readable. Waiting for the health cycle." };

            if (!_options.AllowWrites)
                return state with { TagMessage = "Readable. Writes disabled by CtApi:AllowWrites." };

            var expected = state.Connected ? "1" : "0";

            await client.WriteControlTagAsync(state.ControlTag, expected, ct);

            return state with { TagWriteOk = true, TagMessage = $"Write accepted: {expected}. Read value is from before this write." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ошибка тега не доказывает потерю связи с сервером.
            return state with { TagReadOk = state.TagReadOk ?? false, TagWriteOk = state.TagReadOk == true ? false : null, TagMessage = ex.Message };
        }
    }

    private static bool IsBoolean(string? value) => bool.TryParse(value, out _) || (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && (n == 0 || n == 1));

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