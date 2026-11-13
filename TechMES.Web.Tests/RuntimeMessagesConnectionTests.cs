using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Messages;
using TechMES.Web.Clients;
using TechMES.Web.Settings;

namespace TechMES.Web.Tests;

/// <summary>
/// Проверяет настоящий SignalR-транспорт на локальных Kestrel-серверах.
/// </summary>
public sealed class RuntimeMessagesConnectionTests
{
    /// <summary>
    /// Проверяет смену роли в обе стороны и восстановление подписки.
    /// </summary>
    [Fact]
    public async Task FollowsRolesAndRecreatesSubscription()
    {
        await using var first = await Node.StartAsync("Active");
        await using var second = await Node.StartAsync("Standby");
        await using var services = Services(first, second);

        var live = services.GetRequiredService<RuntimeMessagesConnection>();
        var statuses = new ConcurrentQueue<RuntimeLiveStatus>();
        var messages = new ConcurrentQueue<long?>();

        live.Start(value =>
        {
            messages.Enqueue(value.MessageId);
            return Task.CompletedTask;
        }, value =>
        {
            statuses.Enqueue(value);
            return Task.CompletedTask;
        });

        await WaitAsync(() => statuses.Any(value => value.Connected && value.Endpoint == first.Address));

        await first.SendAsync(1);
        await WaitAsync(() => messages.Count == 1);

        first.Mode = "Standby";
        second.Mode = "Active";

        await WaitAsync(() => statuses.Any(value => value.Connected && value.Endpoint == second.Address));
        await WaitAsync(() => first.Tracker.Connections.IsEmpty);

        await first.SendAsync(2);
        await second.SendAsync(3);
        await WaitAsync(() => messages.Count == 2);

        second.Mode = "Standby";
        first.Mode = "Active";

        await WaitAsync(() => statuses.Count(value => value.Connected && value.Endpoint == first.Address) == 2);

        await first.SendAsync(4);
        await WaitAsync(() => messages.Count == 3);
        await WaitAsync(() => second.Tracker.Connections.IsEmpty);

        Assert.Equal(new long?[] { 1, 3, 4 }, messages.ToArray());
        Assert.Single(first.Tracker.Connections);
        Assert.Empty(second.Tracker.Connections);
    }

    /// <summary>
    /// Повторяет первый запуск после ошибки и восстанавливается после обрыва того же адреса.
    /// </summary>
    [Fact]
    public async Task RetriesStartAndReconnectsSameEndpoint()
    {
        await using var node = await Node.StartAsync("Active");

        node.HubAvailable = false;

        await using var services = Services(node);

        var live = services.GetRequiredService<RuntimeMessagesConnection>();
        var statuses = new ConcurrentQueue<RuntimeLiveStatus>();
        var messages = new ConcurrentQueue<long?>();

        live.Start(value =>
        {
            messages.Enqueue(value.MessageId);
            return Task.CompletedTask;
        }, value =>
        {
            statuses.Enqueue(value);
            return Task.CompletedTask;
        });

        await WaitAsync(() => statuses.Any(value => value.Message.Contains("reconnecting")));

        node.HubAvailable = true;

        await WaitAsync(() => statuses.Count(value => value.Connected) == 1);

        foreach (var context in node.Tracker.Connections.Values)
            context.Abort();

        await WaitAsync(() => statuses.Count(value => value.Connected) == 2);

        await node.SendAsync(42);
        await WaitAsync(() => messages.Count == 1);

        Assert.Equal(42L, messages.Single());
        Assert.Single(node.Tracker.Connections);
    }

    /// <summary>
    /// При двух Active закрывает live-подписки и восстанавливается после устранения конфликта.
    /// </summary>
    [Fact]
    public async Task BlocksAmbiguousActiveState()
    {
        await using var first = await Node.StartAsync("Active");
        await using var second = await Node.StartAsync("Active");
        await using var services = Services(first, second);

        var live = services.GetRequiredService<RuntimeMessagesConnection>();
        var statuses = new ConcurrentQueue<RuntimeLiveStatus>();

        live.Start(_ => Task.CompletedTask, value =>
        {
            statuses.Enqueue(value);
            return Task.CompletedTask;
        });

        await WaitAsync(() => statuses.Any(value => value.Message.Contains("Both Runtime")));

        Assert.Empty(first.Tracker.Connections);
        Assert.Empty(second.Tracker.Connections);

        second.Mode = "Standby";

        await WaitAsync(() => statuses.Any(value => value.Connected));

        second.Mode = "Active";

        await WaitAsync(() => first.Tracker.Connections.IsEmpty);

        Assert.Empty(second.Tracker.Connections);

        first.Mode = "Standby";

        await WaitAsync(() => statuses.Any(value => value.Connected && value.Endpoint == second.Address));
    }

    /// <summary>
    /// После закрытия клиента повторные попытки прекращаются.
    /// </summary>
    [Fact]
    public async Task DisposalStopsRetries()
    {
        await using var node = await Node.StartAsync("Standby");
        await using var services = Services(node);

        var live = services.GetRequiredService<RuntimeMessagesConnection>();
        var statuses = new ConcurrentQueue<RuntimeLiveStatus>();

        live.Start(_ => Task.CompletedTask, value =>
        {
            statuses.Enqueue(value);
            return Task.CompletedTask;
        });

        await WaitAsync(() => statuses.Any());
        await live.DisposeAsync();

        var count = statuses.Count;

        node.Mode = "Active";

        await Task.Delay(TimeSpan.FromSeconds(3.2));

        Assert.Empty(node.Tracker.Connections);
        Assert.Equal(count, statuses.Count);
    }

    /// <summary>
    /// Ошибка перечитывания снимка приводит к повторной синхронизации.
    /// </summary>
    [Fact]
    public async Task RetriesFailedSnapshotCallback()
    {
        await using var node = await Node.StartAsync("Active");
        await using var services = Services(node);

        var live = services.GetRequiredService<RuntimeMessagesConnection>();
        var attempts = 0;

        live.Start(_ => Task.CompletedTask, value =>
        {
            if (value.Connected && Interlocked.Increment(ref attempts) == 1)
                throw new HttpRequestException("Snapshot temporarily unavailable.");

            return Task.CompletedTask;
        });

        await WaitAsync(() => Volatile.Read(ref attempts) == 2);

        Assert.Single(node.Tracker.Connections);
    }

    /// <summary>
    /// Создаёт штатные клиенты. Проверки loopback обходят системный proxy.
    /// </summary>
    private static ServiceProvider Services(Node first, Node? second = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.Configure<RuntimeServiceOptions>(options =>
        {
            options.BaseUrl = first.Address.ToString();
            options.SecondaryBaseUrl = second?.Address.ToString() ?? "";
        });

        services.AddRuntimeServiceRouting();
        services.AddHttpClient(RuntimeEndpointSelector.ProbeClient).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseProxy = false
        });

        services.AddTransient<RuntimeMessagesConnection>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ожидает наблюдаемое состояние с ограничением времени.
    /// </summary>
    private static async Task WaitAsync(Func<bool> ready)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (!ready())
            await Task.Delay(25, stop.Token);
    }

    /// <summary>
    /// Хранит реальные подключения для проверки закрытия и имитации обрыва.
    /// </summary>
    public sealed class Tracker
    {
        public ConcurrentDictionary<string, HubCallerContext> Connections { get; } = new();
    }

    /// <summary>
    /// Тестовый Hub использует контракт уведомлений Runtime.
    /// </summary>
    public sealed class TestHub(Tracker tracker) : Hub
    {
        /// <summary>Запоминает подключение клиента.</summary>
        public override Task OnConnectedAsync()
        {
            tracker.Connections[Context.ConnectionId] = Context;
            return base.OnConnectedAsync();
        }

        /// <summary>Удаляет закрытое подключение.</summary>
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            tracker.Connections.TryRemove(Context.ConnectionId, out _);
            return base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>
    /// Локальный сервер с управляемой ролью и настоящим SignalR endpoint.
    /// </summary>
    private sealed class Node(WebApplication app) : IAsyncDisposable
    {
        public volatile string Mode = "Standby";
        public volatile bool HubAvailable = true;

        public Uri Address { get; private set; } = null!;
        public Tracker Tracker => app.Services.GetRequiredService<Tracker>();

        /// <summary>
        /// Запускает сервер на свободном порту без PostgreSQL и SCADA.
        /// </summary>
        public static async Task<Node> StartAsync(string mode)
        {
            var builder = WebApplication.CreateBuilder();

            // Настройки и сертификаты установленного WEB в тестах не используются.
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSignalR();
            builder.Services.AddSingleton<Tracker>();

            var app = builder.Build();
            var node = new Node(app) { Mode = mode };

            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs/messages") && !node.HubAvailable)
                {
                    context.Response.StatusCode = 503;
                    return;
                }

                await next();
            });

            app.MapGet("/api/runtime/status", () => Results.Ok(new
            {
                mode = node.Mode,
                canExecuteCommands = node.Mode == "Active"
            }));

            app.MapHub<TestHub>("/hubs/messages");

            await app.StartAsync();

            node.Address = new Uri(app.Urls.Single());

            return node;
        }

        /// <summary>Отправляет уведомление через настоящий SignalR.</summary>
        public Task SendAsync(long id) => app.Services.GetRequiredService<IHubContext<TestHub>>().Clients.All.SendAsync("MessagesChanged", new MessageChangedNotification
        {
            MessageId = id
        });

        /// <summary>Останавливает сервер и освобождает порт.</summary>
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}