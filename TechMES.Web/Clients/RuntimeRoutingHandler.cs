using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.Web.Settings;

namespace TechMES.Web.Clients;

public sealed class RuntimeEndpointSelector(IHttpClientFactory clients, IOptions<RuntimeServiceOptions> options)
{
    public const string ProbeClient = "RuntimeProbe";

    // Возвращает настроенные адреса. Одинаковые адреса проверяются один раз.
    public Uri[] GetEndpoints()
    {
        var primary = ParseAddress(options.Value.BaseUrl);
        var secondary = options.Value.SecondaryBaseUrl;

        return string.IsNullOrWhiteSpace(secondary)
            ? [primary]
            : new[] { primary, ParseAddress(secondary) }.Distinct().ToArray();
    }

    /// <summary>
    /// Выбирает единственный готовый Runtime по свежим прямым ответам обоих узлов.
    /// </summary>
    public async Task<Uri> SelectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var states = await ReadStatusesAsync(ct);
        var selected = states.Where(state => state.IsActive).Select(state => state.Endpoint).ToArray();

        return selected.Length switch
        {
            1 => selected[0],
            0 => throw new HttpRequestException("No active Runtime is available. The business request was not sent."),
            _ => throw new HttpRequestException("Both Runtime endpoints report Active. The business request was not sent.")
        };
    }

    /// <summary>
    /// Читает оба узла напрямую, включая Standby.
    /// Общий маршрутизатор рабочих запросов при этом обходится.
    /// </summary>
    public async Task<RuntimeEndpointStatus[]> ReadStatusesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var states = await Task.WhenAll(GetEndpoints().Select(endpoint => ReadStatusAsync(endpoint, ct)));

        ct.ThrowIfCancellationRequested();

        return states;
    }

    /// <summary>Читает Calc и снимок PostgreSQL напрямую, в том числе у Standby.</summary>
    public Task<RuntimeEndpointDiagnostics[]> ReadDiagnosticsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.WhenAll(GetEndpoints().Select(async endpoint =>
        {
            var (value, error) = await ReadProbeAsync<RuntimeDiagnosticsResponse>(endpoint, "api/runtime/diagnostics", ct);
            return new RuntimeEndpointDiagnostics(endpoint, value, error);
        }));
    }

    private async Task<RuntimeEndpointStatus> ReadStatusAsync(Uri endpoint, CancellationToken ct)
    {
        var (value, error) = await ReadProbeAsync<RuntimeActivityStatusResponse>(endpoint, "api/runtime/status", ct);
        return new(endpoint, value, DateTimeOffset.UtcNow, error);
    }

    /// <summary>Ошибки одного endpoint остаются в его снимке. Отмена опроса распространяется вызывающей стороне.</summary>
    private async Task<(T? Value, string Error)> ReadProbeAsync<T>(Uri endpoint, string path, CancellationToken ct) where T : class
    {
        using var client = clients.CreateClient(ProbeClient);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var value = await client.GetFromJsonAsync<T>(new Uri(endpoint, path), timeout.Token);
            return (value, value is null ? "Empty Runtime response." : "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Runtime probe timeout (3 s).");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
        catch (JsonException)
        {
            return (null, "Invalid Runtime response.");
        }
    }

    // Принимает корневой HTTP/HTTPS-адрес сервера без учётных данных и параметров.
    public static Uri ParseAddress(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Runtime URL must be an HTTP/HTTPS server address, for example http://server:5101/.");

        return uri;
    }
}

// Маршрут живёт только в пределах одной загрузки графика.
internal sealed class TagTrendRoute
{
    public const string Path = "api/param/tags/trend";
    public static readonly HttpRequestOptionsKey<TagTrendRoute> Key = new(nameof(TagTrendRoute));

    public Uri? Endpoint { get; set; }
}

public sealed class RuntimeRoutingHandler(RuntimeEndpointSelector selector) : DelegatingHandler
{
    // Сохраняет путь, параметры, тело и заголовки. Автоматического повтора запроса нет.
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var original = request.RequestUri ?? throw new InvalidOperationException("Runtime request URL is missing.");
        var primary = selector.GetEndpoints()[0];

        if (!primary.IsBaseOf(original))
            throw new InvalidOperationException("The Runtime client accepts only its configured API address.");

        TagTrendRoute? trend = null;
        if (request.Method == HttpMethod.Post && original.AbsolutePath == "/" + TagTrendRoute.Path)
            request.Options.TryGetValue(TagTrendRoute.Key, out trend);

        var endpoint = trend?.Endpoint ?? await selector.SelectAsync(ct);
        if (trend is not null)
            trend.Endpoint = endpoint;

        request.RequestUri = new UriBuilder(original)
        {
            Scheme = endpoint.Scheme,
            Host = endpoint.Host,
            Port = endpoint.Port
        }.Uri;

        request.Headers.Host = null;

        return await base.SendAsync(request, ct);
    }
}

public static class RuntimeRoutingServiceCollectionExtensions
{
    // Диагностический клиент обходит маршрутизатор, чтобы проверки не вызывали сами себя.
    public static IServiceCollection AddRuntimeServiceRouting(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeEndpointSelector>();
        services.AddTransient<RuntimeRoutingHandler>();

        services.AddHttpClient(RuntimeEndpointSelector.ProbeClient, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateTransport);

        services.AddHttpClient("RuntimeService", (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<RuntimeServiceOptions>>().Value;

            client.BaseAddress = RuntimeEndpointSelector.ParseAddress(settings.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(CreateTransport)
        .AddHttpMessageHandler<RuntimeRoutingHandler>();

        return services;
    }

    // Запрещает перенос команды через redirect и хранение общих cookies пользователей.
    private static HttpMessageHandler CreateTransport() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };
}
