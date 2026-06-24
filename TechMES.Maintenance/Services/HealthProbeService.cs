using System.Net.Http;
using System.Security.Authentication;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Выполняет легкую HTTP-диагностику WEB и Runtime.Service.
/// Это не заменяет полноценную диагностику зависимостей, но быстро показывает,
/// отвечает ли процесс по ожидаемому URL.
/// </summary>
public sealed class HealthProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Отправляет GET-запрос на указанный URL и возвращает короткий статус для таблицы Dashboard.
    /// Для WEB используем /api/health, а не корневую Blazor-страницу, чтобы проверка была быстрой и стабильной.
    /// </summary>
    public async Task<string> ProbeAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var client = new HttpClient
            {
                Timeout = ProbeTimeout
            };

            using var response = await client.GetAsync(url, timeoutCts.Token);
            return response.IsSuccessStatusCode
                ? $"OK {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Timeout";
        }
        catch (HttpRequestException ex) when (HasAuthenticationError(ex))
        {
            return "TLS certificate error";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>
    /// Определяет, что HTTP-запрос дошел до TLS-этапа, но сертификат не принят ОС.
    /// Для self-signed сертификата это означает, что публичный CER еще не установлен
    /// в доверенные корневые сертификаты текущей машины или клиентского устройства.
    /// </summary>
    private static bool HasAuthenticationError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
                return true;
        }

        return false;
    }
}
