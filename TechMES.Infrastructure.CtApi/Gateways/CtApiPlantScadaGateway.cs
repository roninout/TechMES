using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Application.Scada;
using TechMES.Contracts.Scada;
using TechMES.Infrastructure.CtApi.Native;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Реальный Plant SCADA gateway через CtApi.
///
/// Этот класс уже является полноценным adapter-слоем Runtime.Service.
/// Он:
/// - открывает CtApi при старте;
/// - сериализует все CtApi-вызовы через SemaphoreSlim;
/// - безопасно возвращает ошибки в API;
/// - запрещает запись, если AllowWrites=false;
/// - хранит состояние подключения.
/// </summary>
public sealed class CtApiPlantScadaGateway : IPlantScadaGateway, IAsyncDisposable
{
    /// <summary>
    /// Низкоуровневый wrapper над CtApi.dll. Gateway не вызывает legacy-код напрямую.
    /// </summary>
    private readonly ICtApiNativeClient _nativeClient;

    /// <summary>
    /// Текущие настройки CtApi из appsettings Runtime.Service.
    /// </summary>
    private readonly IOptions<CtApiOptions> _options;

    /// <summary>
    /// Логгер для диагностики подключения, чтения и записи tag-ов.
    /// </summary>
    private readonly ILogger<CtApiPlantScadaGateway> _logger;

    /// <summary>
    /// CtApi не должен вызываться параллельно из разных HTTP-запросов.
    /// Поэтому все вызовы TagRead/TagWrite/Open идут через один gate.
    /// </summary>
    private readonly SemaphoreSlim _apiGate = new(1, 1);

    /// <summary>
    /// Последний известный статус соединения, который отдается endpoint-у диагностики.
    /// </summary>
    private PlantScadaConnectionStatus _status = PlantScadaConnectionStatus.Disconnected;

    /// <summary>
    /// Человекочитаемое описание последнего состояния или ошибки соединения.
    /// </summary>
    private string _lastMessage = "CtApi adapter ещё не инициализирован.";

    /// <summary>
    /// Момент последнего изменения состояния. Пока используется как внутренняя диагностика.
    /// </summary>
    private DateTime _lastStateChangedAt = DateTime.Now;

    /// <summary>
    /// Создает Plant SCADA gateway поверх низкоуровневого CtApi native-клиента.
    /// </summary>
    public CtApiPlantScadaGateway(ICtApiNativeClient nativeClient, IOptions<CtApiOptions> options, ILogger<CtApiPlantScadaGateway> logger)
    {
        _nativeClient = nativeClient;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Открывает CtApi при старте Runtime.Service. Ошибка не валит сервис, а переводит gateway в Disconnected.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            SetState(
                PlantScadaConnectionStatus.Connecting,
                "Открываем CtApi connection...");

            await _nativeClient.OpenAsync(ct);

            SetState(
                PlantScadaConnectionStatus.Connected,
                "CtApi connection открыт.");
        }
        catch (Exception ex)
        {
            SetState(
                PlantScadaConnectionStatus.Disconnected,
                "Ошибка открытия CtApi: " + ex.Message);

            _logger.LogError(
                ex,
                "Не удалось открыть CtApi connection.");

            // ВАЖНО:
            // Пока не валим Runtime.Service при ошибке CtApi.
            // WEB должен запуститься и показать health=Disconnected.
            //
            // Позже можно добавить настройку:
            // CtApi:FailFastOnStartup = true.
        }
        finally
        {
            _apiGate.Release();
        }
    }

    /// <summary>
    /// Возвращает текущее состояние CtApi и при необходимости пробует выполнить probe/reconnect.
    /// </summary>
    public async Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        /*
            Health вызывается:
            - WEB через /api/scada/health;
            - PlantScadaHealthWorker периодически.

            Поэтому здесь можно делать лёгкую probe-проверку.
        */
        if (_status == PlantScadaConnectionStatus.Connected)
        {
            var ok = await ProbeConnectionAsync(ct);

            if (!ok)
            {
                SetState(
                    PlantScadaConnectionStatus.Disconnected,
                    "CtApi probe failed.");

                _logger.LogWarning("CtApi probe failed. Trying reconnect...");

                await TryReconnectAsync(ct);
            }
        }
        else if (_status == PlantScadaConnectionStatus.Disconnected)
        {
            /*
                Если связь уже считается потерянной,
                фоновая проверка будет периодически пытаться восстановить её.
            */
            await TryReconnectAsync(ct);
        }

        return new PlantScadaHealthResponse
        {
            Provider = "CtApi",
            Status = _status,
            IsConnected = _status == PlantScadaConnectionStatus.Connected,
            Message = _lastMessage,
            Time = DateTime.Now
        };
    }

    /// <summary>
    /// Выполняет легкую проверку живости CtApi через native-клиент.
    /// </summary>
    private async Task<bool> ProbeConnectionAsync(CancellationToken ct)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            return await _nativeClient.TryProbeConnectionAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка CtApi probe.");
            return false;
        }
        finally
        {
            _apiGate.Release();
        }
    }

    /// <summary>
    /// Пытается закрыть старое соединение и открыть CtApi заново после ошибки связи.
    /// </summary>
    private async Task TryReconnectAsync(CancellationToken ct)
    {
        await _apiGate.WaitAsync(ct);

        try
        {
            SetState(
                PlantScadaConnectionStatus.Connecting,
                "Пытаемся переподключить CtApi...");

            try
            {
                await _nativeClient.CloseAsync(ct);
            }
            catch
            {
                // Ошибки закрытия при reconnect игнорируем.
            }

            await _nativeClient.OpenAsync(ct);

            SetState(
                PlantScadaConnectionStatus.Connected,
                "CtApi connection restored.");
        }
        catch (Exception ex)
        {
            SetState(
                PlantScadaConnectionStatus.Disconnected,
                "CtApi reconnect failed: " + ex.Message);

            _logger.LogWarning(
                ex,
                "CtApi reconnect failed.");
        }
        finally
        {
            _apiGate.Release();
        }
    }

    /// <summary>
    /// Читает один SCADA tag через CtApi и возвращает контролируемый DTO-ответ для Runtime endpoint-а.
    /// </summary>
    public async Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return new ScadaTagReadResponse
            {
                TagName = tagName,
                Success = false,
                Error = "TagName is required.",
                Time = DateTime.Now
            };
        }

        if (_status != PlantScadaConnectionStatus.Connected)
        {
            return new ScadaTagReadResponse
            {
                TagName = tagName,
                Success = false,
                Error = "CtApi is not connected. " + _lastMessage,
                Time = DateTime.Now
            };
        }

        await _apiGate.WaitAsync(ct);

        try
        {
            var normalizedTagName = tagName.Trim();

            var value = await _nativeClient.TagReadAsync(normalizedTagName, ct);

            return new ScadaTagReadResponse
            {
                TagName = normalizedTagName,
                Value = value,
                Success = true,
                Time = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка TagRead. Tag={TagName}", tagName);

            SetState(PlantScadaConnectionStatus.Disconnected, "Ошибка TagRead: " + ex.Message);

            return new ScadaTagReadResponse
            {
                TagName = tagName,
                Success = false,
                Error = ex.Message,
                Time = DateTime.Now
            };
        }
        finally
        {
            _apiGate.Release();
        }
    }

    /// <summary>
    /// Записывает один SCADA tag, если запись разрешена настройкой CtApi:AllowWrites.
    /// </summary>
    public async Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default)
    {
        var options = _options.Value;

        if (string.IsNullOrWhiteSpace(request.TagName))
        {
            return new ScadaTagWriteResponse
            {
                TagName = request.TagName,
                WrittenValue = request.Value,
                Success = false,
                Error = "TagName is required.",
                Time = DateTime.Now
            };
        }

        if (!options.AllowWrites)
        {
            return new ScadaTagWriteResponse
            {
                TagName = request.TagName,
                WrittenValue = request.Value,
                Success = false,
                Error = "CtApi writes are disabled. Set CtApi:AllowWrites = true to allow writing.",
                Time = DateTime.Now
            };
        }

        if (_status != PlantScadaConnectionStatus.Connected)
        {
            return new ScadaTagWriteResponse
            {
                TagName = request.TagName,
                WrittenValue = request.Value,
                Success = false,
                Error = "CtApi is not connected. " + _lastMessage,
                Time = DateTime.Now
            };
        }

        await _apiGate.WaitAsync(ct);

        try
        {
            var normalizedTagName = request.TagName.Trim();

            await _nativeClient.TagWriteAsync(
                normalizedTagName,
                request.Value,
                ct);

            return new ScadaTagWriteResponse
            {
                TagName = normalizedTagName,
                WrittenValue = request.Value,
                Success = true,
                Time = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка TagWrite. Tag={TagName}, Value={Value}", request.TagName, request.Value);

            SetState(PlantScadaConnectionStatus.Disconnected, "Ошибка TagWrite: " + ex.Message);

            return new ScadaTagWriteResponse
            {
                TagName = request.TagName,
                WrittenValue = request.Value,
                Success = false,
                Error = ex.Message,
                Time = DateTime.Now
            };
        }
        finally
        {
            _apiGate.Release();
        }
    }

    /// <summary>
    /// Закрывает CtApi-соединение при остановке DI-контейнера.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _apiGate.WaitAsync();

        try
        {
            await _nativeClient.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка закрытия CtApi connection.");
        }
        finally
        {
            _apiGate.Release();
            _apiGate.Dispose();
        }
    }

    /// <summary>
    /// Обновляет локальное состояние gateway, которое затем видит endpoint диагностики.
    /// </summary>
    private void SetState(PlantScadaConnectionStatus status, string message)
    {
        _status = status;
        _lastMessage = message;
        _lastStateChangedAt = DateTime.Now;
    }
}
