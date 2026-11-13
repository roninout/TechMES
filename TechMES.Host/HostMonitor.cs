using System.Net.Http.Json;
using System.Text.Json;

namespace TechMES.Host;

/// <summary>Локальная диагностика Host. Изменение сетевых адресов в первом этапе отключено.</summary>
public sealed class HostServiceOptions
{
    public int Port { get; set; } = 5116;
    public string WebHealthUrl { get; set; } = "http://127.0.0.1:5163/api/health";
    public int CheckPeriodSeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 3;
    public bool SharedAddressEnabled { get; set; }

    /// <summary>Проверяет локальный URL и отказывается включать ещё не реализованное управление IP.</summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535 || CheckPeriodSeconds is < 1 or > 60 || TimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("HostService: invalid port or check interval.");
        if (!Uri.TryCreate(WebHealthUrl, UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidOperationException("HostService:WebHealthUrl must be a local HTTP/HTTPS URL without credentials.");
        if (SharedAddressEnabled)
            throw new InvalidOperationException("Shared IP management is not implemented. Keep SharedAddressEnabled=false.");
    }
}

/// <summary>Неизменяемый снимок; Offline означает результат HTTP-проверки, а не состояние SCM.</summary>
public sealed record HostWebStatus(string State, DateTimeOffset? CheckedAtUtc, DateTimeOffset? LastSuccessAtUtc, string Details);

/// <summary>Опрос локального WEB без перенаправлений, общих cookies и изменения сети.</summary>
public sealed class HostMonitor(HttpClient client, HostServiceOptions options, ILogger<HostMonitor> logger) : BackgroundService
{
    private HostWebStatus _status = new("Starting", null, null, "First WEB check is pending.");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    /// <summary>Возвращает последний завершённый снимок для диагностики.</summary>
    public HostWebStatus Status => Volatile.Read(ref _status);

    /// <summary>Готовность зависит от свежего ответа WEB; liveness самого Host проверяется отдельно.</summary>
    public bool IsWebReady => Status is { State: "Online", CheckedAtUtc: { } checkedAt } && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(options.CheckPeriodSeconds + options.TimeoutSeconds + 5);

    /// <summary>Последовательно проверяет WEB до штатной остановки службы.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TechMes Host is observing WEB. Shared IP management is disabled.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CheckPeriodSeconds));
        try
        {
            do { await CheckAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Проверяет HTTP и идентичность WEB. Отмена остановки не превращается в Offline.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        string error;
        try
        {
            using var response = await client.GetAsync(options.WebHealthUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                // Ограничиваем объём диагностического ответа, прежде чем разбирать JSON.
                await response.Content.LoadIntoBufferAsync(16 * 1024, timeout.Token);
                var health = await response.Content.ReadFromJsonAsync<WebHealth>(JsonOptions, timeout.Token);
                if (health is { Service: "TechMES.Web", Status: "OK" })
                {
                    Publish(new("Online", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "WEB health confirmed."));
                    return;
                }
                error = "The URL did not return TechMES.Web health.";
            }
            else error = $"WEB HTTP {(int)response.StatusCode}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "WEB health timeout."; }
        catch (HttpRequestException) { error = "WEB connection or TLS validation failed."; }
        catch (JsonException) { error = "Invalid WEB health JSON."; }
        ct.ThrowIfCancellationRequested();
        Publish(new("Offline", DateTimeOffset.UtcNow, Status.LastSuccessAtUtc, error));
    }

    /// <summary>Публикует снимок атомарно и пишет в журнал только изменение результата.</summary>
    private void Publish(HostWebStatus next)
    {
        var previous = Interlocked.Exchange(ref _status, next);
        if (previous.State != next.State || previous.Details != next.Details)
            logger.LogInformation("WEB state: {State}. {Details}", next.State, next.Details);
    }

    private sealed record WebHealth(string? Service, string? Status);
}
