using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public sealed record PostgreSqlMaintenanceConnection
{
    public string SystemIdentifier { get; init; } = "";
    public string ClientCertificateSha256 { get; init; } = "";
    public string Node131CertificateSha256 { get; init; } = "";
    public string Node132CertificateSha256 { get; init; } = "";
    public int Port { get; init; } = 5115;

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "PostgreSqlMaintenance", "client.json");
    public PostgreSqlControlOptions Control => new() { CommandsEnabled = true, ExpectedSystemIdentifier = SystemIdentifier };
    public PostgreSqlNetworkOptions Network => new() { Enabled = true, NodeAddress = "192.168.1.131", PeerAddress = "192.168.1.132", Port = Port, CertificateSha256 = Node131CertificateSha256, PeerCertificateSha256 = Node132CertificateSha256, MaintenanceCertificateSha256 = ClientCertificateSha256 };

    // Читает закреплённые адреса и сертификаты; строк подключения и SQL-паролей здесь нет.
    public static PostgreSqlMaintenanceConnection Load()
    {
        var connection = PostgreSqlCommandStore.Read<PostgreSqlMaintenanceConnection>(SettingsPath);

        connection.Network.Validate(connection.Control);

        if (connection.ClientCertificateSha256.Length != 64)
            throw new InvalidOperationException("Configure the separate Maintenance client certificate first.");

        return connection;
    }

    // Открывает только собственный клиентский ключ Maintenance из машинного хранилища.
    public X509Certificate2 LoadCertificate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Maintenance certificates require Windows.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

        store.Open(OpenFlags.ReadOnly);

        return store.Certificates.Cast<X509Certificate2>().Single(certificate => certificate.HasPrivateKey && PostgreSqlNodeCertificates.Matches(certificate, ClientCertificateSha256, "1.3.6.1.5.5.7.3.2"));
    }

    // Использует общий локальный журнал координатора, как консольный режим.
    public PostgreSqlCommandStore CreateStore() => new(Options.Create(Control), new PostgreSqlControllerLease());
}

public sealed record PostgreSqlMaintenanceSnapshot(PostgreSqlRemoteStatus[] Nodes, PostgreSqlSwitchHistoryEntry[] History, PostgreSqlSwitchPlan? Pending);

// Смена или закрытие UI не удаляет план; принятая фаза продолжает работу на своём сервере.
public sealed class PostgreSqlMaintenanceSession(IPostgreSqlMaintenanceRemote remote, PostgreSqlCommandStore store, PostgreSqlNetworkOptions network, PostgreSqlControlOptions control)
{
    private string Root => Path.Combine(store.Root, "Network");
    private string PendingPath => Path.Combine(Root, "console-active.json");

    // Получает фактические роли и сохранённую историю с обоих серверов.
    public async Task<PostgreSqlMaintenanceSnapshot> InspectAsync(CancellationToken ct)
    {
        var first = remote.StatusAsync(network.NodeAddress, ct);
        var second = remote.StatusAsync(network.PeerAddress, ct);
        var firstHistory = remote.HistoryAsync(network.NodeAddress, ct);
        var secondHistory = remote.HistoryAsync(network.PeerAddress, ct);

        await Task.WhenAll(first, second, firstHistory, secondHistory);

        var nodes = new[] { await first, await second };
        var history = (await firstHistory).Concat(await secondHistory).ToArray();

        return new(nodes, history, ResolvePending(ReadPending(), nodes));
    }

    // Один незавершённый план должен совпадать на клиенте и на всех зарезервированных узлах.
    public static PostgreSqlSwitchPlan? ResolvePending(PostgreSqlSwitchPlan? local, IEnumerable<PostgreSqlRemoteStatus> nodes)
    {
        var plans = nodes.Select(node => node.ActivePlan).Append(local).Where(plan => plan is not null).Distinct().ToArray();

        if (plans.Length > 1)
            throw new InvalidOperationException("Switch reservations differ. Inspect the existing plans before starting or resuming a switch.");

        return plans.SingleOrDefault();
    }

    // Сохраняет план до Reserve; повтор продолжает прежние OperationId без повторной мутации.
    public async Task RunAsync(string from, string to, bool resume, Action<string> progress, CancellationToken ct)
    {
        network.Validate(control);
        store.Initialize();
        PostgreSqlCommandStore.AssertNoLinks(Root);
        Directory.CreateDirectory(Root);

        if (OperatingSystem.IsWindows())
            new DirectoryInfo(Root).SetAccessControl(new DirectoryInfo(store.Root).GetAccessControl());

        using var held = new FileStream(Path.Combine(Root, "console.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var first = remote.StatusAsync(network.NodeAddress, ct);
        var second = remote.StatusAsync(network.PeerAddress, ct);

        await Task.WhenAll(first, second);

        var nodes = new[] { await first, await second };
        var pending = ResolvePending(ReadPending(), nodes);
        PostgreSqlSwitchPlan plan;

        if (resume)
        {
            plan = pending ?? throw new InvalidOperationException("No pending switch was found.");
        }
        else
        {
            if (pending is not null)
                throw new InvalidOperationException("A switch is already pending. Use Resume; do not create another plan.");

            plan = new(Guid.NewGuid().ToString("N"), from, to, control.ExpectedSystemIdentifier, DateTimeOffset.UtcNow);

            plan.Validate(network, control);
            PostgreSqlSwitchCoordinator.CheckInitial(plan, nodes.Single(node => node.NodeAddress == from), nodes.Single(node => node.NodeAddress == to));
        }

        plan.Validate(network, control);
        PostgreSqlCommandStore.Write(PendingPath, plan);

        void Report(string stage)
        {
            PostgreSqlCommandStore.Write(Path.Combine(Root, "coordinator-" + plan.Id + ".json"), new { plan, stage, checkedAtUtc = DateTimeOffset.UtcNow });
            progress(stage);
        }

        Report((resume ? "RESUME: " : "START: ") + plan.Id);

        await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, Report, ct);

        File.Delete(PendingPath);
    }

    // Локальный план переживает остановку Maintenance и потерю соединения.
    private PostgreSqlSwitchPlan? ReadPending()
    {
        if (!File.Exists(PendingPath))
            return null;

        var plan = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(PendingPath);

        plan.Validate(network, control);

        return plan;
    }
}