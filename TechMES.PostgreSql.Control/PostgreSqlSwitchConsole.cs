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
        if (args.Length == 0 || args[0] is not ("--postgres-status" or "--postgres-switch" or "--postgres-emergency-switch" or "--postgres-emergency-rejoin" or "--postgres-cancel"))
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

            using var held = store.LockCoordinator();
            var activePath = store.CoordinatorPlanPath;

            if (args[0] == "--postgres-cancel")
            {
                var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(activePath);

                foreach (var address in new[] { pending.From, pending.To }.Order(StringComparer.Ordinal))
                    await remote.PlanAsync(address, "cancel", pending, ct);

                File.Delete(activePath);

                Console.WriteLine("Reservation cancelled before the first phase.");

                return true;
            }

            if (args[0] == "--postgres-emergency-rejoin")
            {
                if (args.Length != 2)
                    throw new ArgumentException("Usage: --postgres-emergency-rejoin <existing-plan-id>");

                var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(activePath);
                pending.Validate(network, control);

                if (!pending.Emergency || pending.Id != args[1] || network.NodeAddress != pending.To)
                    throw new InvalidOperationException("Run on the promoted node with the matching retained emergency plan ID.");

                await new PostgreSqlSwitchCoordinator(remote).RecoverEmergencyAsync(pending, stage => ReportProgress(pending, stage), ct);
                File.Delete(activePath);
                return true;
            }

            var emergency = args[0] == "--postgres-emergency-switch";

            if (args.Length != 3)
                throw new ArgumentException("Usage: --postgres-switch|--postgres-emergency-switch <source-IP> <standby-IP>");

            PostgreSqlSwitchPlan plan;

            if (File.Exists(activePath))
            {
                plan = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(activePath);

                if (plan.From != args[1] || plan.To != args[2] || plan.Emergency != emergency)
                    throw new InvalidOperationException("A different console plan is pending. Resume it or inspect its journal.");

                plan.Validate(network, control);

                Console.WriteLine("RESUME: " + plan.Id);
            }
            else
            {
                plan = new(Guid.NewGuid().ToString("N"), args[1], args[2], control.ExpectedSystemIdentifier, DateTimeOffset.UtcNow) { Emergency = emergency };

                plan.Validate(network, control);

                var source = await remote.StatusAsync(plan.From, ct);
                var target = await remote.StatusAsync(plan.To, ct);

                PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target);

                PostgreSqlCommandStore.Write(activePath, plan);

                Console.WriteLine("START: " + plan.Id);
            }

            ReportProgress(plan, "Reservation and sequential phase execution.");
            await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, stage => ReportProgress(plan, stage), ct);

            if (!plan.Emergency)
                File.Delete(activePath);

            void ReportProgress(PostgreSqlSwitchPlan current, string stage)
            {
                store.WriteCoordinatorProgress(current, stage);
                Console.WriteLine(stage);
            }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;

            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("The switch is not confirmed. Retain the plan and operation journals. NeedsReview requires inspection; repeating the command does not retry that phase. If only the client connection was interrupted, resume the same plan.");
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        return true;
    }
}
