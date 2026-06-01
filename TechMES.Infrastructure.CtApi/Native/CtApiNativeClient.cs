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
    /// <summary>
    /// Настройки расположения CtApi.dll, сервера, пользователя и tag-а проверки связи.
    /// </summary>
    private readonly IOptions<CtApiOptions> _options;

    /// <summary>
    /// Логгер низкоуровневых операций CtApi.
    /// </summary>
    private readonly ILogger<CtApiNativeClient> _logger;

    /// <summary>
    /// Legacy wrapper, перенесенный из WPF-проекта.
    /// Все P/Invoke вызовы остаются внутри него.
    /// </summary>
    private readonly LegacyCtApiClient _ctApi;

    /// <summary>
    /// Флаг открытого соединения, чтобы не выполнять TagRead/TagWrite до успешного Open.
    /// </summary>
    private bool _isOpen;

    /// <summary>
    /// Общий gate для всех вызовов старого CtApi wrapper-а.
    /// 
    /// CtApi не должен вызываться параллельно из разных HTTP-запросов:
    /// одновременно может прийти TagRead, Equipment Find и Health Probe.
    /// Поэтому сериализуем вызовы на самом нижнем уровне.
    /// </summary>
    private readonly SemaphoreSlim _apiGate = new(1, 1);

    /// <summary>
    /// Создает native-клиент и подготавливает legacy CtApi wrapper.
    /// </summary>
    public CtApiNativeClient(IOptions<CtApiOptions> options, ILogger<CtApiNativeClient> logger)
    {
        _options = options;
        _logger = logger;

        // Передаём стандартный logger в legacy CtApi wrapper,
        // чтобы не держать отдельный compatibility shim.
        _ctApi = new LegacyCtApiClient(logger);
    }

    /// <summary>
    /// Настраивает DLL search path и открывает соединение CtApi.
    /// </summary>
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

    /// <summary>
    /// Закрывает CtApi-соединение. Метод идемпотентен и не пробрасывает ошибку закрытия наружу.
    /// </summary>
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

    /// <summary>
    /// Читает значение одного tag-а через legacy CtApi wrapper.
    /// </summary>
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

    /// <summary>
    /// Записывает значение одного tag-а через legacy CtApi wrapper.
    /// </summary>
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

    /// <summary>
    /// Выполняет Cicode-команду. Используется для EquipGetProperty, TagInfo, audit и browse helpers.
    /// </summary>
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

    /// <summary>
    /// Выполняет ctFind по таблице Plant SCADA и возвращает строки как словари поле-значение.
    /// </summary>
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

    /// <summary>
    /// Проверяет живость соединения без выбрасывания исключения наружу.
    /// </summary>
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

    /// <summary>
    /// Закрывает соединение и освобождает gate при уничтожении клиента.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _apiGate.Dispose();
    }

    /// <summary>
    /// Защищает все операции чтения/записи от вызова до успешного OpenAsync.
    /// </summary>
    private void EnsureOpen()
    {
        if (!_isOpen)
            throw new InvalidOperationException("CtApi connection is not open.");
    }

    /// <summary>
    /// Формирует команду проверки: либо TagRead(sWndTitle), либо пользовательская команда/tag из настроек.
    /// </summary>
    private static string BuildProbeCommand(string? healthCheckTag)
    {
        if (string.IsNullOrWhiteSpace(healthCheckTag))
            return "TagRead(sWndTitle)";

        var value = healthCheckTag.Trim();

        return value.Contains('(')
            ? value
            : $"TagRead({value})";
    }

    /// <summary>
    /// Windows API для добавления папки Plant SCADA Bin x64 в путь поиска native DLL.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
