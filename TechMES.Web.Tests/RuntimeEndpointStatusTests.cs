using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Web.Clients;
using TechMES.Web.Settings;

namespace TechMES.Web.Tests;

/// <summary>
/// Проверяет диагностику пары независимо от наличия готового Active.
/// </summary>
public sealed class RuntimeEndpointStatusTests
{
    /// <summary>Оба Standby видны, хотя рабочая маршрутизация запрещена.</summary>
    [Fact]
    public async Task ReadsStandbyPairWithoutActive()
    {
        using var services = Services(_ => Status("Standby"));

        var selector = services.GetRequiredService<RuntimeEndpointSelector>();
        var states = await selector.ReadStatusesAsync();

        Assert.Equal(2, states.Length);

        Assert.All(states, state =>
        {
            Assert.Equal("Standby", state.Activity!.Mode);
            Assert.False(state.IsActive);
            Assert.Empty(state.Error);
            Assert.True(state.CheckedAtUtc > DateTimeOffset.UtcNow.AddSeconds(-10));
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => selector.SelectAsync(default));
    }

    /// <summary>Ошибка одного адреса не скрывает состояние второго.</summary>
    [Fact]
    public async Task PreservesOtherNodeWhenOneIsOffline()
    {
        using var services = Services(request => request.RequestUri!.Host == "a" ? throw new HttpRequestException("Connection refused.") : Status("Standby"));

        var states = await services.GetRequiredService<RuntimeEndpointSelector>().ReadStatusesAsync();

        Assert.Null(states[0].Activity);
        Assert.Contains("Connection refused", states[0].Error);
        Assert.Equal("Standby", states[1].Activity!.Mode);
    }

    /// <summary>Два Active видны диагностике, а маршрутизация остаётся заблокированной.</summary>
    [Fact]
    public async Task ShowsBothActiveWhileRoutingIsBlocked()
    {
        using var services = Services(_ => Status("Active"));

        var selector = services.GetRequiredService<RuntimeEndpointSelector>();

        Assert.All(await selector.ReadStatusesAsync(), state => Assert.True(state.IsActive));

        await Assert.ThrowsAsync<HttpRequestException>(() => selector.SelectAsync(default));
    }

    /// <summary>Неправильный ответ помечается ошибкой, второй готовый узел сохраняется.</summary>
    [Fact]
    public async Task InvalidResponseDoesNotHideReadyPeer()
    {
        using var services = Services(request => request.RequestUri!.Host == "a" ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("invalid-json")
        } : Status("Active"));

        var selector = services.GetRequiredService<RuntimeEndpointSelector>();
        var states = await selector.ReadStatusesAsync();

        Assert.Null(states[0].Activity);
        Assert.Contains("Invalid Runtime", states[0].Error);
        Assert.Equal("b", (await selector.SelectAsync(default)).Host);
    }

    /// <summary>Подставляет ответы процесса без реальных IP, БД и SCADA.</summary>
    private static ServiceProvider Services(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.Configure<RuntimeServiceOptions>(options =>
        {
            options.BaseUrl = "http://a:5101/";
            options.SecondaryBaseUrl = "http://b:5101/";
        });

        services.AddRuntimeServiceRouting();
        services.AddHttpClient(RuntimeEndpointSelector.ProbeClient).ConfigurePrimaryHttpMessageHandler(() => new Transport(response));

        return services.BuildServiceProvider();
    }

    /// <summary>Формирует валидный ответ Runtime status.</summary>
    private static HttpResponseMessage Status(string mode) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { mode, canExecuteCommands = mode == "Active" })
    };

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        /// <summary>Возвращает подставной HTTP-ответ.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}