using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using TechMES.Contracts.Host;
using TechMES.Host.Control;

namespace TechMES.Maintenance;

/// <summary>Все команды идут по mTLS; используют сохранённый профиль, а не редактируемые поля.</summary>
public partial class MainWindow
{
    private async void OnHostOwnerStatusClick(object sender, RoutedEventArgs e) =>
        await RunHostOwnerActionAsync("status");

    private async void OnHostOwnerInitializeClick(object sender, RoutedEventArgs e) =>
        await RunHostOwnerActionAsync("initialize");

    private async void OnHostOwnerTransferClick(object sender, RoutedEventArgs e) =>
        await RunHostOwnerActionAsync("transfer");

    private async void OnHostOwnerResumeClick(object sender, RoutedEventArgs e) =>
        await RunHostOwnerActionAsync("resume");

    /// <summary>Сначала читает epoch координатора, затем отправляет команду с защитой от устаревшего состояния.</summary>
    private Task RunHostOwnerActionAsync(string action) => RunHostSetupAsync(async () =>
    {
        RequireHostAdministrator();

        var root = ReadHostSetup();

        var shared = root["HostSharedAddress"]?.Deserialize<HostSharedAddressSettings>(HostJson)
            ?? throw new InvalidDataException("Нет профиля Host.");

        var control = root["HostControl"]?.Deserialize<HostControlSettings>(HostJson) ?? new();
        var healthPort = root["HostService"]?["Port"]?.GetValue<int>() ?? 5116;

        control.Validate(healthPort);

        if (!control.Enabled)
            throw new InvalidOperationException("Сначала включите HostControl и проверьте пару.");

        var coordinator = HostOwnerIdentity.Coordinator(shared);

        var serverPin = coordinator == shared.NodeAddress
            ? control.CertificateSha256
            : control.PeerCertificateSha256;

        var clientSettings = new HostControlSettings
        {
            Enabled = true,
            Port = control.Port,
            CertificateSha256 = control.CertificateSha256,
            PeerCertificateSha256 = serverPin
        };

        using var client = HostNodeCertificates.CreateClient(clientSettings, shared.NodeAddress);
        client.Timeout = TimeSpan.FromSeconds(120);

        var url = new UriBuilder("https", coordinator, control.Port, "/api/host/owner/").Uri;

        var status = await client.GetFromJsonAsync<HostOwnerStatus>(new Uri(url, "status"), HostJson)
            ?? throw new InvalidDataException("Пустой статус координатора.");

        if (status.Pair != HostOwnerIdentity.Pair(shared, control) || status.Node != coordinator)
            throw new InvalidOperationException("Координатор принадлежит другой паре.");

        if (action != "status")
        {
            var request = new HostOwnerRequest(
                action == "initialize" ? shared.NodeAddress : shared.PeerAddress,
                status.Journal.Epoch,
                action == "resume" ? status.Journal.Operation : Guid.NewGuid());

            using var response = await client.PostAsJsonAsync(new Uri(url, action), request, HostJson);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());

            status = await response.Content.ReadFromJsonAsync<HostOwnerStatus>(HostJson)
                ?? throw new InvalidDataException("Пустой результат операции.");
        }

        var local = status;

        if (coordinator != shared.NodeAddress)
        {
            clientSettings.PeerCertificateSha256 = control.CertificateSha256;

            using var localClient = HostNodeCertificates.CreateClient(clientSettings, shared.NodeAddress);

            var localUrl = new UriBuilder("https", shared.NodeAddress, control.Port, "/api/host/owner/status").Uri;

            local = await localClient.GetFromJsonAsync<HostOwnerStatus>(localUrl, HostJson)
                ?? throw new InvalidDataException("Пустой локальный статус Host.");
        }

        HostSetup.Report = JsonSerializer.Serialize(new { Coordinator = status, Local = local }, HostJson);
    });
}