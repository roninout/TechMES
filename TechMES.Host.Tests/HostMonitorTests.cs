using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TechMES.Host;

namespace TechMES.Host.Tests;

public sealed class HostMonitorTests
{
    [Theory]
    [InlineData("http://192.168.1.118:5163/api/health")]
    [InlineData("http://user:password@localhost:5163/api/health")]
    [InlineData("file:///tmp/health")]
    [InlineData("relative")]
    public void RejectsNonLocalOrCredentialedProbe(string url) => Assert.Throws<InvalidOperationException>(() => new HostServiceOptions { WebHealthUrl = url }.Validate());

    [Fact]
    public void SharedIpCannotBeEnabledAccidentally() => Assert.Throws<InvalidOperationException>(() => new HostServiceOptions { SharedAddressEnabled = true }.Validate());

    [Fact]
    public async Task CorrectWebIdentityBecomesReady()
    {
        using var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"service\":\"TechMES.Web\",\"status\":\"OK\"}")));
        using var monitor = Monitor(client);
        Assert.False(monitor.IsWebReady);
        await monitor.CheckAsync(default);
        Assert.True(monitor.IsWebReady);
        Assert.NotNull(monitor.Status.LastSuccessAtUtc);
    }

    [Theory]
    [InlineData("{\"service\":\"AnotherService\",\"status\":\"OK\"}")]
    [InlineData("{\"service\":\"TechMES.Web\",\"status\":\"Error\"}")]
    [InlineData("not JSON")]
    public async Task Http200AloneDoesNotProveWebHealth(string body)
    {
        using var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));
        using var monitor = Monitor(client);
        await monitor.CheckAsync(default);
        Assert.Equal("Offline", monitor.Status.State);
        Assert.False(monitor.IsWebReady);
    }

    [Fact]
    public async Task FailurePreservesLastSuccess()
    {
        var count = 0;
        using var client = Client(_ => Task.FromResult(++count == 1 ? Json(HttpStatusCode.OK, "{\"service\":\"TechMES.Web\",\"status\":\"OK\"}") : Json(HttpStatusCode.ServiceUnavailable, "")));
        using var monitor = Monitor(client);
        await monitor.CheckAsync(default);
        var success = monitor.Status.LastSuccessAtUtc;
        await monitor.CheckAsync(default);
        Assert.False(monitor.IsWebReady);
        Assert.Equal(success, monitor.Status.LastSuccessAtUtc);
        Assert.Contains("503", monitor.Status.Details);
    }

    [Fact]
    public async Task TimeoutIsReportedAsOffline()
    {
        using var client = Client(async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(HttpStatusCode.OK, ""); });
        using var monitor = Monitor(client, 1);
        await monitor.CheckAsync(default);
        Assert.Equal("Offline", monitor.Status.State);
        Assert.Contains("timeout", monitor.Status.Details);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishFalseOffline()
    {
        using var client = Client(async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(HttpStatusCode.OK, ""); });
        using var monitor = Monitor(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.CheckAsync(cancellation.Token));
        Assert.Equal("Starting", monitor.Status.State);
    }

    [Fact]
    public async Task OversizedHealthResponseIsRejected()
    {
        using var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 17000))));
        using var monitor = Monitor(client);
        await monitor.CheckAsync(default);
        Assert.Equal("Offline", monitor.Status.State);
    }

    private static HostMonitor Monitor(HttpClient client, int timeout = 3) => new(client, new HostServiceOptions { TimeoutSeconds = timeout }, NullLogger<HostMonitor>.Instance);
    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }
}
