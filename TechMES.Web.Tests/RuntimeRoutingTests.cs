using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Web.Clients;
using TechMES.Web.Settings;

namespace TechMES.Web.Tests;

public sealed class RuntimeRoutingTests
{
    // Выбор зависит от фактической активности, а не только от доступности HTTP.
    [Theory]
    [InlineData("Active", "Standby", "a")]
    [InlineData("Standby", "Active", "b")]
    [InlineData("offline", "Active", "b")]
    [InlineData("Active", "offline", "a")]
    [InlineData("invalid", "Active", "b")]
    [InlineData("503", "Active", "b")]
    [InlineData("denied", "Active", "b")]
    public async Task SelectsOnlyActiveRuntime(string first, string second, string expected)
    {
        using var services = Services(request => Task.FromResult(Status(request.RequestUri!.Host == "a" ? first : second)));
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");
        using var response = await client.GetAsync("api/recipes/catalog");

        Assert.Equal(expected, response.RequestMessage!.RequestUri!.Host);
    }

    // Ноль или два наблюдаемых Active блокируют рабочий запрос.
    [Theory]
    [InlineData("Standby", "Standby")]
    [InlineData("offline", "offline")]
    [InlineData("Active", "Active")]
    [InlineData("Starting", "Draining")]
    public async Task AmbiguousStateBlocksBusinessRequest(string first, string second)
    {
        var sends = 0;

        using var services = Services(request => Task.FromResult(Status(request.RequestUri!.Host == "a" ? first : second)), request =>
        {
            sends++;
            return Task.FromResult(Reply(request));
        });

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsJsonAsync("api/recipes/47/values", new { value = 4 }));

        Assert.Equal(0, sends);
    }

    // Следующий запрос использует новый Active без кэширования прежнего состояния.
    [Fact]
    public async Task FollowsRoleChange()
    {
        var current = "a";

        using var services = Services(request => Task.FromResult(Status(request.RequestUri!.Host == current ? "Active" : "Standby")));
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");
        using var first = await client.GetAsync("api/recipes/catalog");

        current = "b";

        using var second = await client.GetAsync("api/recipes/catalog");

        Assert.Equal("a", first.RequestMessage!.RequestUri!.Host);
        Assert.Equal("b", second.RequestMessage!.RequestUri!.Host);
    }

    // Ошибка отправленной команды возвращается без повтора; тело и заголовки сохраняются.
    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task SendsCommandOnce(int code)
    {
        var sends = 0;

        using var services = Services(request => Task.FromResult(Status(request.RequestUri!.Host == "b" ? "Active" : "Standby")), async request =>
        {
            sends++;

            Assert.Equal("b", request.RequestUri!.Host);
            Assert.Equal("/api/recipes/47/values?check=1", request.RequestUri.PathAndQuery);
            Assert.Equal("{\"value\":4}", await request.Content!.ReadAsStringAsync());
            Assert.Equal("tester", request.Headers.GetValues("X-TechMES-User").Single());

            if (code == 0)
                throw new HttpRequestException("Disconnected after send.");

            return Reply(request, (HttpStatusCode)code);
        });

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");
        using var command = new HttpRequestMessage(HttpMethod.Post, "api/recipes/47/values?check=1")
        {
            Content = JsonContent.Create(new { value = 4 })
        };

        command.Headers.Add("X-TechMES-User", "tester");

        if (code == 0)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(command));
        }
        else
        {
            using var response = await client.SendAsync(command);

            Assert.Equal(code, (int)response.StatusCode);
        }

        Assert.Equal(1, sends);
    }

    // Отмена во время проверок прерывает работу до отправки команды.
    [Fact]
    public async Task CancellationDoesNotSendCommand()
    {
        using var stop = new CancellationTokenSource();

        var sends = 0;

        using var services = Services(request =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }, request =>
        {
            sends++;
            return Task.FromResult(Reply(request));
        });

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("api/recipes/catalog", stop.Token));

        Assert.Equal(0, sends);
    }

    // Один адрес поддерживается; дубликат не считается двумя Active.
    [Theory]
    [InlineData("")]
    [InlineData("http://a:5101/")]
    public async Task OneEndpointIsCheckedOnce(string secondary)
    {
        var probes = 0;

        using var services = Services(request =>
        {
            probes++;
            return Task.FromResult(Status("Active"));
        }, secondary: secondary);

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");
        using var response = await client.GetAsync("api/recipes/catalog");

        Assert.Equal(1, probes);
    }

    // Произвольный внешний адрес не получает запрос через Runtime-клиент.
    [Fact]
    public async Task RejectsForeignAddress()
    {
        using var services = Services(request => throw new InvalidOperationException("Must not probe."));
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("RuntimeService");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://foreign/api/recipes/catalog"));
    }

    // Подменяет только транспорт; штатная DI-цепочка маршрутизатора остаётся в тесте.
    private static ServiceProvider Services(Func<HttpRequestMessage, Task<HttpResponseMessage>> probe, Func<HttpRequestMessage, Task<HttpResponseMessage>>? send = null, string secondary = "http://b:5101/")
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.Configure<RuntimeServiceOptions>(options =>
        {
            options.BaseUrl = "http://a:5101/";
            options.SecondaryBaseUrl = secondary;
        });

        services.AddRuntimeServiceRouting();
        services.AddHttpClient(RuntimeEndpointSelector.ProbeClient).ConfigurePrimaryHttpMessageHandler(() => new Transport(probe));
        services.AddHttpClient("RuntimeService").ConfigurePrimaryHttpMessageHandler(() => new Transport(send ?? (request => Task.FromResult(Reply(request)))));

        return services.BuildServiceProvider();
    }

    // Эмулирует состояние процесса без подключения к реальным серверам.
    private static HttpResponseMessage Status(string mode) => mode switch
    {
        "offline" => throw new HttpRequestException("Connection refused."),
        "invalid" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        },
        "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                mode = mode == "denied" ? "Active" : mode,
                canExecuteCommands = mode == "Active"
            })
        }
    };

    // Позволяет проверить фактический адрес рабочего запроса.
    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        RequestMessage = request
    };

    private sealed class Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        // Возвращает ответ подставного транспорта.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
}