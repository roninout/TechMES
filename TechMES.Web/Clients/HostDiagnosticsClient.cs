using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Host;

namespace TechMES.Web.Clients;

/// <summary>Снимок локального Host, который обслуживает текущий экземпляр WEB.</summary>
public sealed record HostDiagnosticsState(string Service = "", string Computer = "", string Mode = "", bool SharedAddressEnabled = false, bool WebReady = false, HostOwnerStatus? Ownership = null);

/// <summary>
/// Читает только локальную диагностику Host. Клиент принадлежит панели и освобождается вместе с ней.
/// </summary>
public sealed class HostDiagnosticsClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly HttpClient _client;

    /// <summary>Loopback не использует proxy, cookies и перенаправления.</summary>
    public HostDiagnosticsClient(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Ограничивает время и размер ответа; произвольный сетевой адрес не принимается.</summary>
    public async Task<HostDiagnosticsState> ReadAsync(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("HostDiagnostics:Port должен быть от 1 до 65535.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        using var response = await _client.GetAsync($"http://127.0.0.1:{port}/api/host/status", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
        var state = await response.Content.ReadFromJsonAsync<HostDiagnosticsState>(JsonOptions, timeout.Token);

        if (state is null || state.Service != "TechMES.Host" || string.IsNullOrWhiteSpace(state.Computer) || state.Mode is not ("Observer" or "Planned" or "AutoWeb"))
            throw new InvalidDataException("Локальный endpoint вернул некорректный ответ TechMES.Host.");

        if (state.SharedAddressEnabled != (state.Ownership is not null))
            throw new InvalidDataException("Host вернул противоречивый режим управления общим IP.");

        if (state.Ownership is { } owner && (owner.Grant is null || owner.Journal is null || owner.Errors is null || string.IsNullOrWhiteSpace(owner.Node) || string.IsNullOrWhiteSpace(owner.Coordinator)))
            throw new InvalidDataException("Host не вернул корректное состояние управления общим IP.");

        return state;
    }

    /// <summary>Завершает HTTP-клиент и принадлежащий ему handler.</summary>
    public void Dispose() => _client.Dispose();
}