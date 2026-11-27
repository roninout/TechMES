using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TechMES.Contracts.Param;
using TechMES.Contracts.Runtime;
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

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task FullTrendDayProbesUnavailablePeerOnce(string active)
    {
        var probes = 0;
        var sends = 0;
        using var services = Services(request =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(Status(request.RequestUri!.Host == active ? "Active" : "offline"));
        }, request =>
        {
            Assert.Equal(active, request.RequestUri!.Host);
            sends++;
            return Task.FromResult(TrendResponse());
        });

        var param = TrendClient(services);
        var route = new TagTrendRoute();
        var day = DateTime.UtcNow.Date.AddDays(-1);
        for (var hour = 0; hour < 24; hour++)
            await param.GetTagTrendAsync("Trend", day.AddHours(hour), day.AddHours(hour + 1), route, default);

        Assert.Equal(24, sends);
        Assert.Equal(2, probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrendRepeatsOnlyInterruptedChunkAfterRoleChange(bool disconnected)
    {
        var active = "a";
        var probes = 0;
        var sends = new List<string>();
        var ranges = new List<ParamTagTrendRequest>();
        using var services = Services(request =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(Status(request.RequestUri!.Host == active ? "Active" : "Standby"));
        }, async request =>
        {
            var node = request.RequestUri!.Host;
            sends.Add(node);
            ranges.Add((await request.Content!.ReadFromJsonAsync<ParamTagTrendRequest>())!);
            if (node == active)
                return TrendResponse();

            if (disconnected)
                throw new HttpRequestException("Connection refused.");

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new { code = "runtime.not-active" })
            };
        });

        var param = TrendClient(services);
        var route = new TagTrendRoute();
        var day = DateTime.UtcNow.Date.AddDays(-1);
        await param.GetTagTrendAsync("Trend", day, day.AddHours(1), route, default);
        active = "b";
        await param.GetTagTrendAsync("Trend", day.AddHours(1), day.AddHours(2), route, default);
        await param.GetTagTrendAsync("Trend", day.AddHours(2), day.AddHours(3), route, default);

        Assert.Equal(new[] { "a", "a", "b", "b" }, sends);
        Assert.Equal(ranges[1].FromUtc, ranges[2].FromUtc);
        Assert.Equal(ranges[1].ToUtc, ranges[2].ToUtc);
        Assert.Equal(4, probes);
    }

    [Fact]
    public async Task TrendDoesNotRetryDatabaseBlock()
    {
        var sends = 0;
        using var services = Services(_ => Task.FromResult(Status("Active")), _ =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new { code = "database.write-blocked" })
            });
        }, secondary: "");

        var now = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            TrendClient(services).GetTagTrendAsync("Trend", now.AddHours(-1), now, new TagTrendRoute(), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(1, sends);
    }

    private static ParamApiClient TrendClient(ServiceProvider services) =>
        new(services.GetRequiredService<IHttpClientFactory>(), new AnonymousAuth(), new HttpContextAccessor());

    private static HttpResponseMessage TrendResponse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ParamTrendResponse { Supported = true })
    };

    private sealed class AnonymousAuth : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));
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

    // Недоступность диагностики на старом Runtime не скрывает снимок резервного узла.
    [Fact]
    public async Task DiagnosticsKeepsStandbyWhenFirstRuntimeHasNoDiagnosticsEndpoint()
    {
        using var services = Services(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/runtime/diagnostics", request.RequestUri!.AbsolutePath);

            if (request.RequestUri.Host == "a")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var snapshot = new RuntimeDiagnosticsResponse(new("Standby", "Running", null, null), new() { Enabled = true });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) });
        }, request => throw new InvalidOperationException("Diagnostics must use the direct client."));

        var results = await services.GetRequiredService<RuntimeEndpointSelector>().ReadDiagnosticsAsync();

        Assert.Equal(2, results.Length);
        Assert.Null(results[0].Snapshot);
        Assert.Equal("Standby", results[1].Snapshot?.Calc.Status);
        Assert.True(results[1].Snapshot?.PostgreSql.Enabled ?? false);
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
