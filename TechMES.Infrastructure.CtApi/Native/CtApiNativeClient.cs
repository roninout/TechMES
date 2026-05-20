using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using TechMES.Infrastructure.CtApi.Settings;
using LegacyCtApiClient = global::CtApi.CtApi;
using LegacyCtOpen = global::CtApi.CtOpen;

namespace TechMES.Infrastructure.CtApi.Native;

/// <summary>
/// Низкоуровневый клиент для реального CtApi.dll.
///
/// Этот класс — единственное место в TechMES, где напрямую используется
/// старый WPF wrapper CtApi.CtApi.
///
/// Runtime.Service и WEB работают выше — через IPlantScadaGateway.
/// </summary>
public sealed class CtApiNativeClient : ICtApiNativeClient, IAsyncDisposable
{
    private readonly IOptions<CtApiOptions> _options;
    private readonly ILogger<CtApiNativeClient> _logger;

    private readonly LegacyCtApiClient _ctApi;

    private bool _isOpen;

    /// <summary>
    /// Общий gate для всех вызовов старого CtApi wrapper-а.
    /// 
    /// CtApi не должен вызываться параллельно из разных HTTP-запросов:
    /// одновременно может прийти TagRead, Equipment Find и Health Probe.
    /// Поэтому сериализуем вызовы на самом нижнем уровне.
    /// </summary>
    private readonly SemaphoreSlim _apiGate = new(1, 1);

    public CtApiNativeClient(IOptions<CtApiOptions> options, ILogger<CtApiNativeClient> logger)
    {
        _options = options;
        _logger = logger;

        // Передаём стандартный logger в legacy CtApi wrapper,
        // чтобы не держать отдельный compatibility shim.
        _ctApi = new LegacyCtApiClient(logger);
    }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            var options = _options.Value;

            if (string.IsNullOrWhiteSpace(options.Path))
            {
                throw new InvalidOperationException(
                    "CtApi:Path is empty. Укажите путь к Plant SCADA Bin x64.");
            }

            if (!Directory.Exists(options.Path))
            {
                throw new DirectoryNotFoundException(
                    $"CtApi:Path не существует: {options.Path}");
            }

            SetDllDirectory(options.Path);

            _ctApi.SetCtApiDirectory(options.Path);

            _logger.LogInformation(
                "Opening CtApi. Path={Path}, Server={Server}, User={User}",
                options.Path,
                options.Server,
                options.User);

            if (string.IsNullOrWhiteSpace(options.Server))
            {
                await _ctApi.OpenAsync(
                    computer: null,
                    user: null,
                    password: null,
                    mode: LegacyCtOpen.Reconnect);
            }
            else
            {
                await _ctApi.OpenAsync(
                    computer: options.Server,
                    user: options.User,
                    password: options.Password,
                    mode: 0);
            }

            _isOpen = true;

            _logger.LogInformation("CtApi opened successfully.");
        }
        catch
        {
            _isOpen = false;
            throw;
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            if (_isOpen)
            {
                _ctApi.Close();
                _logger.LogInformation("CtApi closed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CtApi close failed.");
        }
        finally
        {
            _isOpen = false;
            _apiGate.Release();
        }
    }

    public async Task<string?> TagReadAsync(string tagName, CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            EnsureOpen();

            if (string.IsNullOrWhiteSpace(tagName))
                throw new ArgumentException("Tag name is required.", nameof(tagName));

            var normalizedTagName = tagName.Trim();

            return await _ctApi.TagReadAsync(normalizedTagName);
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async Task TagWriteAsync(string tagName, string? value, CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            EnsureOpen();

            if (string.IsNullOrWhiteSpace(tagName))
                throw new ArgumentException("Tag name is required.", nameof(tagName));

            var normalizedTagName = tagName.Trim();

            await _ctApi.TagWriteAsync(
                normalizedTagName,
                value ?? "");
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async Task<string?> CicodeAsync(string command, CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            EnsureOpen();

            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Cicode command is required.", nameof(command));

            return await _ctApi.CicodeAsync(command.Trim());
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async Task<IReadOnlyList<Dictionary<string, string>>> FindAsync(string tableName, string? filter, string? cluster, IReadOnlyList<string> properties, CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            EnsureOpen();

            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Table name is required.", nameof(tableName));

            if (properties.Count == 0)
                throw new ArgumentException("At least one property is required.", nameof(properties));

            var result = await _ctApi.FindAsync(
                tableName.Trim(),
                filter,
                cluster,
                properties.ToArray());

            return result.ToList();
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async Task<bool> TryProbeConnectionAsync(CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            if (!_isOpen)
                return false;

            var options = _options.Value;
            var probeCommand = BuildProbeCommand(options.HealthCheckTag);

            return await _ctApi.TryProbeConnectionAsync(probeCommand);
        }
        catch
        {
            return false;
        }
        finally
        {
            _apiGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _apiGate.Dispose();
    }

    private void EnsureOpen()
    {
        if (!_isOpen)
            throw new InvalidOperationException("CtApi connection is not open.");
    }

    private static string BuildProbeCommand(string? healthCheckTag)
    {
        if (string.IsNullOrWhiteSpace(healthCheckTag))
            return "TagRead(sWndTitle)";

        var value = healthCheckTag.Trim();

        return value.Contains('(')
            ? value
            : $"TagRead({value})";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}