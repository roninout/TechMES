using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

// Принимает только фиксированные фазы; произвольной командной строки и путей в API нет.
public sealed class PostgreSqlSwitchInbox(PostgreSqlCommandStore store, PostgreSqlNodeMonitor monitor, IPostgreSqlActionHost host, IOptions<PostgreSqlControlOptions> control, IOptions<PostgreSqlNetworkOptions> network)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string NetworkRoot => Path.Combine(store.Root, "Network");

    private string ActivePath => Path.Combine(NetworkRoot, "active-switch.json");

    // Создаёт закрытый каталог для API, консольного координатора и журнала.
    public void Initialize()
    {
        store.Initialize();

        PostgreSqlCommandStore.AssertNoLinks(NetworkRoot);

        Directory.CreateDirectory(NetworkRoot);

        if (OperatingSystem.IsWindows())
            new DirectoryInfo(NetworkRoot).SetAccessControl(new DirectoryInfo(store.Root).GetAccessControl());
    }

    // Возвращает наблюдение; само по себе оно не разрешает повышение реплики.
    public PostgreSqlRemoteStatus Status()
    {
        return new(network.Value.NodeAddress, monitor.GetHealth());
    }

    // Резервирует узел для одного плана до первой мутации.
    public async Task ReserveAsync(PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        plan.Validate(network.Value, control.Value);

        await _gate.WaitAsync(ct);

        try
        {
            Initialize();

            if (File.Exists(CompletedPath(plan)))
            {
                if (PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(CompletedPath(plan)) != plan)
                    throw new InvalidOperationException("A different immutable plan uses this Id.");

                return;
            }

            if (File.Exists(ActivePath))
            {
                if (PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath) != plan)
                    throw new InvalidOperationException("Another switch is reserved. Resume or inspect that switch.");

                return;
            }

            await host.ExecuteOwnedAsync(_ =>
            {
                AssertNoOutstandingCommands();

                PostgreSqlCommandStore.Write(ActivePath, plan);

                return Task.CompletedTask;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Сохраняет фазу один раз; повтор запроса возвращает ту же локальную операцию.
    public async Task<PostgreSqlRemoteOperation> SubmitAsync(string mode, PostgreSqlPhaseRequest phase, CancellationToken ct)
    {
        var plan = phase.Plan;

        plan.Validate(network.Value, control.Value);
        AssertMode(mode);

        if ((mode == "Promote" ? plan.To : plan.From) != network.Value.NodeAddress || (mode == "Fence" ? phase.Fence is not null : phase.Fence is not { ValueKind: JsonValueKind.Object }))
            throw new InvalidOperationException("Phase, node or fence proof mismatch.");

        await _gate.WaitAsync(ct);

        try
        {
            Initialize();

            var directory = OperationDirectory(plan, mode);
            var requestPath = Path.Combine(directory, "request.json");

            if (File.Exists(requestPath))
            {
                var known = File.Exists(CompletedPath(plan)) && PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(CompletedPath(plan)) == plan || File.Exists(ActivePath) && PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath) == plan;

                if (!known)
                    throw new InvalidOperationException("The original immutable plan is missing or differs.");

                var existing = PostgreSqlCommandStore.Read<PostgreSqlCommand>(requestPath);

                if (existing.Mode != mode || existing.From != plan.From || existing.To != plan.To || existing.SystemIdentifier != plan.SystemIdentifier || ProofText(existing.Fence) != ProofText(phase.Fence))
                    throw new InvalidOperationException("An immutable phase request already exists with different content.");

                return Read(plan, mode);
            }

            AssertActive(plan);

            await host.ExecuteOwnedAsync(_ =>
            {
                AssertActive(plan);

                Directory.CreateDirectory(directory);

                if (OperatingSystem.IsWindows())
                    new DirectoryInfo(directory).SetAccessControl(new DirectoryInfo(store.Root).GetAccessControl());

                PostgreSqlCommandStore.Write(requestPath, new PostgreSqlCommand(plan.OperationId(mode), mode, plan.From, plan.To, plan.SystemIdentifier, DateTimeOffset.UtcNow, phase.Fence));

                return Task.CompletedTask;
            }, ct);

            return Read(plan, mode);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Отдаёт готовый результат и proof; логины и пароли между узлами не передаются.
    public PostgreSqlRemoteOperation Read(PostgreSqlSwitchPlan plan, string mode)
    {
        plan.Validate(network.Value, control.Value);
        AssertMode(mode);

        var directory = OperationDirectory(plan, mode);

        if (!File.Exists(Path.Combine(directory, "request.json")))
            return new(plan.OperationId(mode), "Missing", "The phase has not been submitted.", null, null);

        var journalPath = Path.Combine(directory, "journal.json");

        if (!File.Exists(journalPath))
            return new(plan.OperationId(mode), "Queued", "Waiting for the local executor.", null, null);

        var journal = PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(journalPath);

        if (journal.State != "Succeeded")
            return new(journal.Id, journal.State, journal.Message, null, null);

        var result = PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(directory, "script-result.json"));
        JsonElement? proof = mode == "Fence" ? PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(directory, "fence.json")) : null;

        return new(journal.Id, journal.State, journal.Message, result, proof);
    }

    // Освобождает план после подтверждения всех локальных фаз этого узла.
    public async Task CompleteAsync(PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        plan.Validate(network.Value, control.Value);

        await _gate.WaitAsync(ct);

        try
        {
            Initialize();

            if (File.Exists(CompletedPath(plan)))
            {
                if (PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(CompletedPath(plan)) != plan)
                    throw new InvalidOperationException("Completed plan identity mismatch.");

                if (File.Exists(ActivePath) && PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath) == plan)
                    File.Delete(ActivePath);

                return;
            }

            AssertActive(plan);

            var modes = network.Value.NodeAddress == plan.From ? new[] { "Fence", "Rejoin" } : new[] { "Promote" };

            if (modes.Any(mode => Read(plan, mode).State != "Succeeded"))
                throw new InvalidOperationException("Local switch phases are not confirmed. Reservation retained.");

            PostgreSqlCommandStore.Write(CompletedPath(plan), plan);

            File.Delete(ActivePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Отменяет только резервирование, для которого ещё не была подана ни одна фаза.
    public async Task CancelAsync(PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        plan.Validate(network.Value, control.Value);

        await _gate.WaitAsync(ct);

        try
        {
            Initialize();

            if (!File.Exists(ActivePath))
                return;

            AssertActive(plan);

            if (new[] { "Fence", "Promote", "Rejoin" }.Any(mode => File.Exists(Path.Combine(OperationDirectory(plan, mode), "request.json"))))
                throw new InvalidOperationException("A phase was already submitted. Cancellation is refused; resume or inspect the operation.");

            File.Delete(ActivePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Во время сетевого переключения запрещает обход через локальную ручную очередь.
    public static void AssertCommandAllowed(string root, PostgreSqlCommand request)
    {
        var active = Path.Combine(root, "Network", "active-switch.json");

        if (!File.Exists(active))
            return;

        var plan = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(active);

        if (request.Id != plan.OperationId(request.Mode) || request.From != plan.From || request.To != plan.To || request.SystemIdentifier != plan.SystemIdentifier)
            throw new InvalidOperationException("This node is reserved by another switch. Manual commands are blocked.");
    }

    // Не забирает узел с ожидающей, работающей или неопределённой прежней командой.
    private void AssertNoOutstandingCommands()
    {
        foreach (var directory in Directory.GetDirectories(store.Root))
        {
            var request = Path.Combine(directory, "request.json");

            if (!File.Exists(request))
                continue;

            var journal = Path.Combine(directory, "journal.json");

            if (!File.Exists(journal) || PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(journal).State is not ("Succeeded" or "Rejected"))
                throw new InvalidOperationException("An outstanding local command must be inspected first.");
        }
    }

    // Требует совпадение с неизменным зарезервированным планом.
    private void AssertActive(PostgreSqlSwitchPlan plan)
    {
        if (!File.Exists(ActivePath) || PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath) != plan)
            throw new InvalidOperationException("The switch has not reserved this node.");
    }

    // Использует только фиксированные имена фаз.
    private static void AssertMode(string mode)
    {
        if (mode is not ("Fence" or "Promote" or "Rejoin"))
            throw new InvalidOperationException("Unknown PostgreSQL phase.");
    }

    // Хранит завершённый план для безопасного повтора подтверждения.
    private string CompletedPath(PostgreSqlSwitchPlan plan)
    {
        return Path.Combine(NetworkRoot, "completed-" + plan.Id + ".json");
    }

    // Вычисляет каталог фазы без пользовательского пути.
    private string OperationDirectory(PostgreSqlSwitchPlan plan, string mode)
    {
        return Path.Combine(store.Root, plan.OperationId(mode));
    }

    // Сравнивает содержимое proof после нормализации JSON.
    private static string ProofText(JsonElement? proof)
    {
        return proof is null ? "" : JsonSerializer.Serialize(proof.Value);
    }
}