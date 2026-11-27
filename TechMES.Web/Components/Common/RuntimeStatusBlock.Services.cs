using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Radzen;
using TechMES.Contracts.Host;
using TechMES.Web.Clients;

namespace TechMES.Web.Components.Common;

public partial class RuntimeStatusBlock
{
    private readonly HostDiagnosticsClient _serviceProbe = new();
    private NodeServices[] _nodeServices = [];
    private string _serviceError = "";
    private bool _isServiceLoading;

    private sealed record WebProbe(Uri Endpoint, string Error);
    private sealed record HostProbe(bool? Online, string Details, HostOwnerStatus? Ownership = null);
    private sealed record NodeServices(WebProbe Web, HostProbe Host, DateTimeOffset CheckedAtUtc);

    private async Task RefreshNodeServicesAsync()
    {
        if (_disposed || _isServiceLoading)
            return;

        _isServiceLoading = true;

        try
        {
            var ct = _stop.Token;
            var endpoints = RuntimeEndpoints.GetEndpoints();
            var listener = GetWebListener();
            var host = ReadLocalHostAsync(ct);
            var nodes = await Task.WhenAll(endpoints.Select(async runtime =>
            {
                var web = ReadWebProbeAsync(runtime, listener, ct);
                var addresses = ResolveAddressesAsync(runtime, ct);
                return (Web: await web, Addresses: await addresses);
            }));
            var (local, hostError) = await host;
            ct.ThrowIfCancellationRequested();

            // Перенос VIP не меняет строки: каждая привязана к адресу своего Runtime.
            var localAddresses = local?.Ownership is null ? ReadLocalAddresses() : [];
            var now = DateTimeOffset.UtcNow;
            _nodeServices = nodes.Select(node => new NodeServices(
                node.Web, MatchHost(node.Addresses, localAddresses, local, hostError, now), now)).ToArray();
            _serviceError = "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _nodeServices = [];
            _serviceError = ex.Message;
        }
        finally
        {
            _isServiceLoading = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task<(HostDiagnosticsState? State, string Error)> ReadLocalHostAsync(CancellationToken ct)
    {
        try
        {
            return (await _serviceProbe.ReadAsync(Configuration.GetValue("HostDiagnostics:Port", 5116), ct), "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, ex is OperationCanceledException ? "Локальный Host не ответил за 3 секунды." : ex.Message);
        }
    }

    private async Task<WebProbe> ReadWebProbeAsync(Uri runtime, Uri listener, CancellationToken ct)
    {
        var endpoint = new UriBuilder(listener) { Host = runtime.IdnHost, Path = "/api/health", Query = "", Fragment = "" }.Uri;
        try
        {
            await _serviceProbe.ReadWebAsync(endpoint, ct);
            return new(endpoint, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(endpoint, ex is OperationCanceledException ? "WEB не ответил за 3 секунды." : ex.Message);
        }
    }

    private Uri GetWebListener()
    {
        var https = Configuration.GetValue("HttpsRedirection:Enabled", false);
        var preferred = https ? "https" : "http";
        var configured = new[] { Configuration["Kestrel:Endpoints:Http:Url"], Configuration["Kestrel:Endpoints:Https:Url"] }
            .Concat((Configuration["Urls"] ?? "").Split(';'))
            .Select(value => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri?.Scheme is "http" or "https")
            .ToArray();
        return configured.FirstOrDefault(uri => uri!.Scheme == preferred)
            ?? configured.FirstOrDefault()
            ?? new Uri(https ? "https://127.0.0.1:7163/" : "http://127.0.0.1:5163/");
    }

    private static async Task<IPAddress[]> ResolveAddressesAsync(Uri runtime, CancellationToken ct)
    {
        if (IPAddress.TryParse(runtime.IdnHost, out var address))
            return [address];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await Dns.GetHostAddressesAsync(runtime.IdnHost, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SocketException or OperationCanceledException)
        {
            return [];
        }
    }

    private static HostProbe MatchHost(IPAddress[] addresses, HashSet<IPAddress> localAddresses,
        HostDiagnosticsState? local, string localError, DateTimeOffset now)
    {
        if (local?.Peer is { } peer && IPAddress.TryParse(peer.Node, out var peerAddress) && addresses.Contains(peerAddress))
        {
            if (peer.CheckedAtUtc is not { } checkedAt || !IsServiceFresh(checkedAt, now))
                return new(null, "Ожидание свежего ответа второго Host по mTLS.");

            if (peer.Status is { } status)
                return new(true, $"Ответ второго Host по mTLS · {checkedAt.ToLocalTime():HH:mm:ss}", status);

            return new(false, $"Связь со вторым Host по mTLS · {checkedAt.ToLocalTime():HH:mm:ss}\n{peer.Error}");
        }

        var localNode = local?.Ownership?.Node;
        var match = addresses.Any(IPAddress.IsLoopback) || (IPAddress.TryParse(localNode, out var nodeAddress)
            ? addresses.Contains(nodeAddress)
            : addresses.Any(localAddresses.Contains));

        return match
            ? local is not null
                ? new(true, $"Локальный Host · {local.Computer} · {local.Mode}", local.Ownership)
                : new(false, localError)
            : new(null, "Нет снимка Host для адреса этого Runtime.");
    }

    private static HashSet<IPAddress> ReadLocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address).ToHashSet();
        }
        catch (NetworkInformationException)
        {
            // WEB проверяется и там, где получение сетевых интерфейсов недоступно.
            return [];
        }
    }

    private static bool IsServiceFresh(DateTimeOffset at, DateTimeOffset now) => now - at < TimeSpan.FromSeconds(15);

    private NodeServices? NodeServicesAt(int index)
    {
        var node = _nodeServices.ElementAtOrDefault(index);
        return node is not null && IsServiceFresh(node.CheckedAtUtc, DateTimeOffset.UtcNow) ? node : null;
    }

    private (string Text, BadgeStyle Style, string Hint) DescribeWeb(int index)
    {
        if (NodeServicesAt(index) is not { } node)
            return DescribeMissingNodeServices(index);

        var hint = $"{node.Web.Endpoint}\nПроверено: {node.CheckedAtUtc.ToLocalTime():HH:mm:ss}";
        return node.Web.Error.Length == 0
            ? ("Online", BadgeStyle.Success, hint)
            : ("Unavailable", BadgeStyle.Danger, $"{hint}\n{node.Web.Error}");
    }

    private (string Text, BadgeStyle Style, string Hint) DescribeHost(int index)
    {
        if (NodeServicesAt(index) is not { } node)
            return DescribeMissingNodeServices(index);

        var host = node.Host;
        return host.Online switch
        {
            true => ("Online", BadgeStyle.Success, BuildHostHint(host)),
            false => ("Unavailable", BadgeStyle.Danger, host.Details),
            _ => ("Unknown", BadgeStyle.Light, host.Details)
        };
    }

    private (string Text, BadgeStyle Style, string Hint) DescribeMissingNodeServices(int index)
    {
        if (index == 1 && (_nodeServices.Length == 1 || _states.Length == 1))
            return ("N/A", BadgeStyle.Light, "Второй отдельный адрес Runtime отсутствует.");

        if (_serviceError.Length > 0)
            return ("Unknown", BadgeStyle.Warning, _serviceError);

        return _nodeServices.Length == 0
            ? ("Checking", BadgeStyle.Light, "Ожидание данных.")
            : ("Stale", BadgeStyle.Warning, "Ожидание свежих данных.");
    }

    private static (string Text, BadgeStyle Style) DescribeVip(HostOwnerStatus? owner)
    {
        if (owner is null)
            return ("Disabled", BadgeStyle.Base);
        if (!owner.NetworkReady || owner.Errors.Length > 0 || (owner.AddressPresent && !owner.Grant.Assigned) || (owner.AddressReady && !owner.AddressPresent))
            return ("Error", BadgeStyle.Danger);
        if (!owner.Grant.Assigned)
            return ("Standby", BadgeStyle.Info);
        return owner.AddressReady ? ("Owner", BadgeStyle.Success) : ("Pending", BadgeStyle.Warning);
    }

    private static string BuildHostHint(HostProbe host)
    {
        var text = new StringBuilder(host.Details);
        if (host.Ownership is not { } owner)
            return text.AppendLine().Append("Управление VIP отключено.").ToString();

        text.AppendLine().AppendLine($"Узел: {owner.Node} · координатор: {owner.Coordinator}");
        text.AppendLine($"Сеть готова: {(owner.NetworkReady ? "да" : "нет")} · WEB по данным Host: {(owner.WebReady ? "готов" : "не готов")}");
        text.AppendLine($"VIP: {(owner.AddressPresent ? "присутствует" : "отсутствует")} · готов: {(owner.AddressReady ? "да" : "нет")} · epoch: {owner.Grant.Epoch}");

        if (owner.Node == owner.Coordinator)
        {
            var journal = owner.Journal;
            var reason = journal.AutomaticHostTransfer ? "отказ Host" : journal.AutomaticWebFailover ? "отказ WEB" : "ручная операция";
            text.AppendLine($"Владелец по журналу: {journal.Owner} · фаза: {journal.Phase} · epoch: {journal.Epoch}");
            if (journal.Phase != "Empty")
                text.AppendLine($"Причина передачи: {reason}");
            if (owner.WebFailover is { } auto)
            {
                text.AppendLine($"Автоматика WEB: {auto.State} · отказы владельца: {auto.OwnerFailures} · готовность резерва: {auto.TargetSuccesses}");
                text.AppendLine(auto.Details);
            }
        }

        foreach (var error in owner.Errors)
            text.AppendLine($"Ошибка: {error}");

        return text.ToString();
    }
}
