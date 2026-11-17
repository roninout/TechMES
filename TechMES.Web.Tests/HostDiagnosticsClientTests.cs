using System.Net;
using System.Net.Http.Json;
using TechMES.Contracts.Host;
using TechMES.Web.Clients;

namespace TechMES.Web.Tests;

/// <summary>Проверяет локальную диагностику без реальных служб, VIP и сетевых адресов узлов.</summary>
public sealed class HostDiagnosticsClientTests
{
    /// <summary>Ведомый узел сохраняет свой grant; пустой локальный журнал не означает потерю владельца.</summary>
    [Fact]
    public async Task ReadsFollowerWithEmptyLocalJournal()
    {
        Uri? requested = null;
        var owner = new HostOwnerStatus("pair", "node-b", "node-a", true, true, [], true, true, new(20, true, true), new(), new(false, "Disabled", 0, 0, "Coordinator only."));

        using var client = new HostDiagnosticsClient(new Transport((request, _) =>
        {
            requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostDiagnosticsState("TechMES.Host", "server-b", "Planned", true, true, owner))
            });
        }));

        var state = await client.ReadAsync(15116);

        Assert.Equal("http://127.0.0.1:15116/api/host/status", requested!.AbsoluteUri);
        Assert.Equal(20, state.Ownership!.Grant.Epoch);
        Assert.True(state.Ownership.Grant.Assigned);
        Assert.True(state.Ownership.AddressReady);
        Assert.Equal("Empty", state.Ownership.Journal.Phase);
        Assert.False(state.Ownership.WebFailover!.Enabled);
    }

    /// <summary>Наблюдатель является доступным Host без включённого управления VIP.</summary>
    [Fact]
    public async Task AcceptsObserverWithoutOwnership()
    {
        using var client = new HostDiagnosticsClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new HostDiagnosticsState("TechMES.Host", "server", "Observer"))
        })));

        var state = await client.ReadAsync(5116);

        Assert.False(state.SharedAddressEnabled);
        Assert.Null(state.Ownership);
    }

    /// <summary>HTTP 200 другого сервиса и повреждённые ответы не подтверждают доступность Host.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"service\":\"TechMES.Web\",\"computer\":\"server\",\"mode\":\"Observer\"}")]
    [InlineData("{\"service\":\"TechMES.Host\",\"computer\":\"server\",\"mode\":\"Planned\",\"sharedAddressEnabled\":true}")]
    [InlineData("{\"service\":\"TechMES.Host\",\"computer\":\"server\",\"mode\":\"Observer\",\"ownership\":{}}")]
    public async Task RejectsWrongOrIncompleteResponse(string json)
    {
        using var client = new HostDiagnosticsClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        })));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(5116));
    }

    /// <summary>Ошибка HTTP не превращается в пустой успешный снимок.</summary>
    [Fact]
    public async Task RejectsHttpFailure()
    {
        using var client = new HostDiagnosticsClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(5116));
    }

    /// <summary>Зависший запрос ограничен собственным трёхсекундным таймаутом.</summary>
    [Fact]
    public async Task CancelsUnresponsiveHost()
    {
        using var client = new HostDiagnosticsClient(new Transport(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(5116));
    }

    /// <summary>Закрытие панели отменяет запрос, не дожидаясь сетевого таймаута.</summary>
    [Fact]
    public async Task PreservesCallerCancellation()
    {
        using var stop = new CancellationTokenSource();
        using var client = new HostDiagnosticsClient(new Transport(async (_, ct) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(5116, stop.Token));
    }

    /// <summary>Диагностический ответ имеет ограниченный размер.</summary>
    [Fact]
    public async Task RejectsOversizedResponse()
    {
        using var client = new HostDiagnosticsClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 65 * 1024))
        })));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(5116));
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        /// <summary>Подставляет ответ локального процесса.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request, ct);
    }
}