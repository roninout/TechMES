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

    // Возвращает наблюдение и существующее резервирование без запуска проверок или команд.
    public PostgreSqlRemoteStatus Status()
    {
        return new(network.Value.NodeAddress, monitor.GetHealth(), File.Exists(ActivePath) ? PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath) : null);
    }

    // Запускает только диагностику на узле с действующим владением и без активного плана.
    public async Task<PostgreSqlSwitchReadiness> CheckSwitchReadinessAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("The local PostgreSQL controller is busy. Try the check again.");

        try
        {
            if (File.Exists(ActivePath))
                throw new InvalidOperationException("An active PostgreSQL switch already exists.");

            var node = network.Value.NodeAddress;
            var peer = network.Value.PeerAddress;
            var (from, to) = monitor.GetHealth().PostgreSqlRole switch
            {
                "Primary" => (node, peer),
                "Standby" => (peer, node),
                _ => throw new InvalidOperationException("The local PostgreSQL role is unknown.")
            };

            (bool Ready, string Detail) result = (false, "");
            await host.ExecuteOwnedAsync(async settings =>
            {
                AssertNoOutstandingCommands();
                result = await PostgreSqlSwitchRunner.CheckSwitchReadinessAsync(settings, from, to, store, ct);
            }, ct);

            return new(node, from, to, result.Ready, result.Detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Возвращает активный план и последние двадцать завершённых планов без изменений на диске.
    public PostgreSqlSwitchHistoryEntry[] History()
    {
        if (!Directory.Exists(NetworkRoot))
            return [];

        PostgreSqlCommandStore.AssertPrivate(NetworkRoot, true);

        var plans = Directory.GetFiles(NetworkRoot, "completed-*.json").Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^completed-[0-9a-f]{32}\\.json$")).OrderByDescending(File.GetLastWriteTimeUtc).Take(20).Select(PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>).ToList();

        if (File.Exists(ActivePath))
        {
            var active = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(ActivePath);

            plans.RemoveAll(plan => plan.Id == active.Id);
            plans.Insert(0, active);
        }

        return plans.Select(plan =>
        {
            plan.Validate(network.Value, control.Value);

            var modes = network.Value.NodeAddress != plan.From ? new[] { "Promote" }
                : plan.Emergency ? new[] { "Fence", "Rejoin", "Rebuild" } : new[] { "Fence", "Rejoin" };
            var phases = modes.Select(mode =>
            {
                var operation = Read(plan, mode);
                var result = operation.Result;

                string? Field(string name) => result is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { } text ? text[..Math.Min(text.Length, 2048)] : null;

                return new PostgreSqlPhaseSummary(mode, operation.Id, operation.State, operation.Message, Field("Error"), Field("Log"));
            }).ToArray();

            return new PostgreSqlSwitchHistoryEntry(plan, network.Value.NodeAddress, File.Exists(CompletedPath(plan)), phases);
        }).ToArray();
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

        if ((mode == "Rebuild" && !plan.Emergency) || (mode == "Promote" ? plan.To : plan.From) != network.Value.NodeAddress || (mode == "Fence" ? phase.Fence is not null : phase.Fence is not { ValueKind: JsonValueKind.Object }))
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

                if (existing.Mode != mode || existing.From != plan.From || existing.To != plan.To || existing.SystemIdentifier != plan.SystemIdentifier || existing.EmergencyFenceId != EmergencyFenceId(plan) || ProofText(existing.Fence) != ProofText(phase.Fence))
                    throw new InvalidOperationException("An immutable phase request already exists with different content.");

                return Read(plan, mode);
            }

            AssertActive(plan);
            AssertRecoveryChoice(store.Root, plan, mode, phase.Fence);

            await host.ExecuteOwnedAsync(_ =>
            {
                AssertActive(plan);

                Directory.CreateDirectory(directory);

                if (OperatingSystem.IsWindows())
                    new DirectoryInfo(directory).SetAccessControl(new DirectoryInfo(store.Root).GetAccessControl());

                var request = new PostgreSqlCommand(plan.OperationId(mode), mode, plan.From, plan.To, plan.SystemIdentifier, DateTimeOffset.UtcNow, phase.Fence)
                {
                    EmergencyFenceId = EmergencyFenceId(plan)
                };

                PostgreSqlCommandStore.Write(requestPath, request);

                return Task.CompletedTask;
            }, ct);

            return Read(plan, mode);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Отдаёт сохранённый результат; NeedsReview показывает ошибку и остаётся заблокированным.
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

        if (journal.Id != plan.OperationId(mode))
            throw new IOException("Operation journal identity mismatch.");

        if (journal.State != "Succeeded")
        {
            JsonElement? failed = null;
            var message = journal.Message;
            var resultPath = Path.Combine(directory, "script-result.json");

            if (journal.State is ("NeedsReview" or "RebuildRequired") && File.Exists(resultPath))
            {
                try
                {
                    failed = PostgreSqlCommandStore.Read<JsonElement>(resultPath);

                    if (failed.Value.ValueKind == JsonValueKind.Object && failed.Value.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
                        message += " Script error: " + text[..Math.Min(text.Length, 2048)];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    message += $" Saved result cannot be read ({ex.GetType().Name}).";
                }
            }

            return new(journal.Id, journal.State, message, failed, null);
        }

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

            var onSource = network.Value.NodeAddress == plan.From;
            var confirmed = onSource
                ? Read(plan, "Fence").State == "Succeeded" && (Read(plan, "Rejoin").State == "Succeeded" || (plan.Emergency && Read(plan, "Rebuild").State == "Succeeded"))
                : Read(plan, "Promote").State == "Succeeded";
            if (!confirmed)
                throw new InvalidOperationException("Local switch phases are not confirmed. Reservation retained.");

            // Новый Primary сохраняет резервирование, пока прежний узел не подтвердит восстановление.
            if (plan.Emergency && !onSource)
            {
                using var certificate = PostgreSqlNodeCertificates.LoadLocal(network.Value);
                using var remote = new PostgreSqlSwitchRemote(network.Value, certificate);
                await PostgreSqlSwitchCoordinator.VerifyEmergencyRecoveryAsync(remote, plan, ct);
            }

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

            if (new[] { "Fence", "Promote", "Rejoin", "Rebuild" }.Any(mode => File.Exists(Path.Combine(OperationDirectory(plan, mode), "request.json"))))
                throw new InvalidOperationException("A phase was already submitted. Cancellation is refused; resume or inspect the operation.");

            File.Delete(ActivePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Требует привязку аварийной команды к активному сетевому плану.
    public static PostgreSqlSwitchPlan? AssertCommandAllowed(string root, PostgreSqlCommand request)
    {
        var active = Path.Combine(root, "Network", "active-switch.json");

        if (!File.Exists(active))
        {
            if (request.Mode == "Rebuild" || request.EmergencyFenceId is not null)
                throw new InvalidOperationException("An emergency command requires a reserved network plan.");

            return null;
        }

        var plan = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(active);

        if (request.Id != plan.OperationId(request.Mode) || request.From != plan.From || request.To != plan.To || request.SystemIdentifier != plan.SystemIdentifier || request.EmergencyFenceId != EmergencyFenceId(plan))
            throw new InvalidOperationException("This command does not match the reserved switch.");

        AssertRecoveryChoice(root, plan, request.Mode, request.Fence);
        return plan;
    }

    // Запрещает конкурирующие способы восстановления и сверяет Rebuild с локальным журналом Fence.
    private static void AssertRecoveryChoice(string root, PostgreSqlSwitchPlan plan, string mode, JsonElement? proof)
    {
        var rebuildPath = Path.Combine(root, plan.OperationId("Rebuild"), "request.json");
        if (mode == "Rejoin" && File.Exists(rebuildPath))
            throw new InvalidOperationException("Rebuild was selected for this plan. Inspect or resume Rebuild.");

        if (mode != "Rebuild")
            return;

        if (!plan.Emergency || proof is null)
            throw new InvalidOperationException("Rebuild requires the reserved emergency plan and its Fence proof.");

        var rejoinPath = Path.Combine(root, plan.OperationId("Rejoin"));
        if (File.Exists(Path.Combine(rejoinPath, "request.json")))
        {
            var journalPath = Path.Combine(rejoinPath, "journal.json");
            if (!File.Exists(journalPath))
                throw new InvalidOperationException("Rejoin is queued. Inspect its outcome before Rebuild.");

            var journal = PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(journalPath);
            if (journal.Id != plan.OperationId("Rejoin") || journal.State is not ("Rejected" or "RebuildRequired"))
                throw new InvalidOperationException("Rejoin has not confirmed that Rebuild is safe. Inspect the existing operation.");
        }

        var fenceId = plan.OperationId("Fence");
        var fencePath = Path.Combine(root, fenceId);
        var fence = PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(Path.Combine(fencePath, "journal.json"));
        var recorded = PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(fencePath, "fence.json"));
        if (fence.Id != fenceId || fence.State != "Succeeded" || !JsonElement.DeepEquals(recorded, proof.Value))
            throw new InvalidOperationException("Rebuild Fence proof does not match the confirmed local operation.");
    }

    private static string? EmergencyFenceId(PostgreSqlSwitchPlan plan) => plan.Emergency ? plan.OperationId("Fence") : null;

    // Не забирает узел с ожидающей, работающей или неопределённой прежней командой.
    private void AssertNoOutstandingCommands()
    {
        foreach (var directory in Directory.GetDirectories(store.Root))
        {
            var request = Path.Combine(directory, "request.json");

            if (!File.Exists(request))
                continue;

            var journal = Path.Combine(directory, "journal.json");

            if (!File.Exists(journal) || PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(journal).State is not ("Succeeded" or "Rejected" or "RebuildRequired"))
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
        if (mode is not ("Fence" or "Promote" or "Rejoin" or "Rebuild"))
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
