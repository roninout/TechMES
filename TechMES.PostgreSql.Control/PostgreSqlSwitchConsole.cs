using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

// Консольный режим того же apphost; отдельная служба координатора не создаётся.
public static class PostgreSqlSwitchConsole
{
    // До запуска Generic Host обрабатывает status/switch/cancel.
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--postgres-status" or "--postgres-switch" or "--postgres-cancel"))
            return false;

        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler cancel = (_, value) =>
        {
            value.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;

        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Run on a configured Windows database node as Administrator.");

            var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.PostgreSqlControl.json", optional: false).Build();
            var control = configuration.GetSection(PostgreSqlControlOptions.SectionName).Get<PostgreSqlControlOptions>() ?? new();
            var network = configuration.GetSection(PostgreSqlNetworkOptions.SectionName).Get<PostgreSqlNetworkOptions>() ?? new();

            network.Validate(control);

            using var certificate = PostgreSqlNodeCertificates.LoadLocal(network);
            using var remote = new PostgreSqlSwitchRemote(network, certificate);

            var ct = cancellation.Token;

            if (args[0] == "--postgres-status")
            {
                foreach (var address in new[] { network.NodeAddress, network.PeerAddress })
                {
                    var status = await remote.StatusAsync(address, ct);

                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(status, PostgreSqlCommandStore.Json));
                }

                return true;
            }

            var store = new PostgreSqlCommandStore(Options.Create(control), new PostgreSqlControllerLease());

            store.Initialize();

            var root = Path.Combine(store.Root, "Network");

            Directory.CreateDirectory(root);

            if (OperatingSystem.IsWindows())
                System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), System.IO.FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(store.Root)));

            using var held = new FileStream(Path.Combine(root, "console.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var activePath = Path.Combine(root, "console-active.json");

            if (args[0] == "--postgres-cancel")
            {
                var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(activePath);

                foreach (var address in new[] { pending.From, pending.To }.Order(StringComparer.Ordinal))
                    await remote.PlanAsync(address, "cancel", pending, ct);

                File.Delete(activePath);

                Console.WriteLine("Reservation cancelled before the first phase.");

                return true;
            }

            if (args.Length != 3)
                throw new ArgumentException("Usage: --postgres-switch <current-Primary-IP> <current-Standby-IP>");

            PostgreSqlSwitchPlan plan;

            if (File.Exists(activePath))
            {
                plan = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(activePath);

                if (plan.From != args[1] || plan.To != args[2])
                    throw new InvalidOperationException("A different console plan is pending. Resume it or inspect its journal.");

                plan.Validate(network, control);

                Console.WriteLine("RESUME: " + plan.Id);
            }
            else
            {
                plan = new(Guid.NewGuid().ToString("N"), args[1], args[2], control.ExpectedSystemIdentifier, DateTimeOffset.UtcNow);

                plan.Validate(network, control);

                var source = await remote.StatusAsync(plan.From, ct);
                var target = await remote.StatusAsync(plan.To, ct);

                PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target);

                PostgreSqlCommandStore.Write(activePath, plan);

                Console.WriteLine("START: " + plan.Id);
            }

            var journalPath = Path.Combine(root, "coordinator-" + plan.Id + ".json");

            void Progress(string stage)
            {
                PostgreSqlCommandStore.Write(journalPath, new { plan, stage, checkedAtUtc = DateTimeOffset.UtcNow });

                Console.WriteLine(stage);
            }

            Progress("Reservation and sequential phase execution.");

            await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, Progress, ct);

            File.Delete(activePath);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;

            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("An accepted phase continues on its node. Repeat the same command from this host to resume the same plan. Do not delete operation journals.");
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        return true;
    }
}