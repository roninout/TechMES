using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public interface IPostgreSqlSwitchRemote
{
    Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct);
    Task PlanAsync(string address, string action, PostgreSqlSwitchPlan plan, CancellationToken ct);
    Task<PostgreSqlRemoteOperation> SubmitAsync(string address, string mode, PostgreSqlPhaseRequest phase, CancellationToken ct);
    Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct);
}

public interface IPostgreSqlMaintenanceRemote : IPostgreSqlSwitchRemote
{
    Task<PostgreSqlSwitchHistoryEntry[]> HistoryAsync(string address, CancellationToken ct);
}

// Клиент предъявляет свой сертификат и проверяет pin и IP сервера.
public sealed class PostgreSqlSwitchRemote : IPostgreSqlMaintenanceRemote, IDisposable
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    // Создаёт клиентов только для двух настроенных адресов.
    public PostgreSqlSwitchRemote(PostgreSqlNetworkOptions options, X509Certificate2 certificate, TimeSpan? timeout = null)
    {
        foreach (var address in new[] { options.NodeAddress, options.PeerAddress })
        {
            var pin = address == options.NodeAddress ? options.CertificateSha256 : options.PeerCertificateSha256;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, server, _, errors) => PostgreSqlNodeCertificates.AcceptServer(server, pin, errors)
            };

            handler.ClientCertificates.Add(certificate);

            _clients.Add(address, new HttpClient(handler) { BaseAddress = new Uri($"https://{address}:{options.Port}/"), Timeout = timeout ?? TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 1048576 });
        }
    }

    // Получает состояние узла без запуска мутаций.
    public async Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct)
    {
        return await _clients[address].GetFromJsonAsync<PostgreSqlRemoteStatus>("v1/status", ct) ?? throw new IOException("Empty node status.");
    }

    // Получает результат диагностики с закреплённого mTLS-узла.
    public async Task<PostgreSqlSwitchReadiness> CheckSwitchReadinessAsync(string address, CancellationToken ct)
    {
        using var response = await _clients[address].GetAsync("v1/switch-readiness", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new IOException("На узле установлена версия контроллера без проверки готовности. Обновите Runtime или Agent.");

        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<PostgreSqlSwitchReadiness>(ct) ?? throw new IOException("Контроллер не вернул результат диагностики.");
    }

    // Читает историю с узла, включая этапы и сохранённую причину отказа.
    public async Task<PostgreSqlSwitchHistoryEntry[]> HistoryAsync(string address, CancellationToken ct)
    {
        return await _clients[address].GetFromJsonAsync<PostgreSqlSwitchHistoryEntry[]>("v1/switches", ct) ?? throw new IOException("Empty switch history.");
    }

    // Использует фиксированные маршруты управления планом.
    public async Task PlanAsync(string address, string action, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        if (action is not ("reserve" or "complete" or "cancel"))
            throw new ArgumentException("Unknown plan action.");

        using var response = await _clients[address].PostAsJsonAsync("v1/" + action, plan, ct);

        await CheckAsync(response, ct);
    }

    // Повтор POST имеет тот же OperationId; новый скрипт не создаётся.
    public async Task<PostgreSqlRemoteOperation> SubmitAsync(string address, string mode, PostgreSqlPhaseRequest phase, CancellationToken ct)
    {
        using var response = await _clients[address].PostAsJsonAsync("v1/phases/" + mode, phase, ct);

        await CheckAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<PostgreSqlRemoteOperation>(ct) ?? throw new IOException("Empty phase response.");
    }

    // Читает результат существующей операции без повторного исполнения.
    public async Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        using var response = await _clients[address].PostAsJsonAsync("v1/read/" + mode, plan, ct);

        await CheckAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<PostgreSqlRemoteOperation>(ct) ?? throw new IOException("Empty operation response.");
    }

    // Возвращает ограниченное сообщение об отказе узла.
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new IOException($"Node HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 2048)]}");
    }

    // Освобождает оба HTTP-клиента.
    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
    }
}

// Выполняет последовательные фазы по сохранённому плану.
public sealed class PostgreSqlSwitchCoordinator(IPostgreSqlSwitchRemote remote)
{
    // Требует свежих снимков от обоих контроллеров; для аварийного плана исходная служба уже остановлена.
    public static void CheckInitial(PostgreSqlSwitchPlan plan, PostgreSqlRemoteStatus source, PostgreSqlRemoteStatus target)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var node in new[] { source, target })
        {
            var local = node.Local;
            var serviceMatches = plan.Emergency && node == source
                ? IsStopped(local.Service)
                : local.Service?.ManagedByService == true;

            if (local.CheckedAtUtc is null || now - local.CheckedAtUtc > TimeSpan.FromSeconds(Math.Max(60, 2 * local.HealthPeriodSeconds)) || local.CheckedAtUtc > now.AddMinutes(1) || local.ControllerState != "Monitoring" || !local.OwnsController || !local.PermissionsOk || !serviceMatches || !local.Settings.CommandsEnabled || local.Settings.ExpectedSystemIdentifier != plan.SystemIdentifier || local.SystemIdentifier != plan.SystemIdentifier)
                throw new InvalidOperationException("Both controllers must confirm fresh service state, ownership, commands and the expected cluster.");
        }

        var sourceRoleMatches = plan.Emergency
            ? source.Local.PostgreSqlRole == "Stopped" && source.Local.ClusterState is ("in production" or "shut down")
            : source.Local.PostgreSqlRole == "Primary";

        if (source.NodeAddress != plan.From || target.NodeAddress != plan.To || !sourceRoleMatches || target.Local.PostgreSqlRole != "Standby" || source.Local.Timeline != target.Local.Timeline || source.Local.Timeline is null)
            throw new InvalidOperationException("Expected the selected source and its Standby in the same timeline. Local checks verify the service, SQL and WAL.");
    }

    private static bool IsStopped(PostgreSqlServiceState? service) => service is { ServiceState: "Stopped", ServiceProcessId: 0, PostmasterProcessId: 0, ListenerProcessId: 0 };

    // Перед выполнением аварийного Promote новый узел сам читает исходное подтверждение по mTLS.
    public static async Task VerifyEmergencyFenceAsync(IPostgreSqlSwitchRemote remote, PostgreSqlSwitchPlan plan, JsonElement proof)
    {
        var fence = await remote.ReadAsync(plan.From, "Fence", plan, CancellationToken.None);

        if (fence.State != "Succeeded" || fence.Id != plan.OperationId("Fence") || fence.Fence is not { } original || !JsonElement.DeepEquals(original, proof))
            throw new IOException("The source controller did not confirm the submitted emergency Fence.");

        CheckFenceProof(plan, original);
    }

    // Связывает аварийное доказательство именно с Fence данного плана.
    private static void CheckFenceProof(PostgreSqlSwitchPlan plan, JsonElement proof)
    {
        if (proof.GetProperty("State").GetString() != "Fenced" || proof.GetProperty("From").GetString() != plan.From || proof.GetProperty("To").GetString() != plan.To || proof.GetProperty("Sid").GetString() != plan.SystemIdentifier)
            throw new IOException("The fence proof does not match the selected plan.");

        if (plan.Emergency && (!proof.TryGetProperty("Format", out var format) || !format.TryGetInt32(out var version) || version != 2 || !proof.TryGetProperty("Id", out var id) || id.GetString() != plan.OperationId("Fence")))
            throw new IOException("Emergency Fence proof does not belong to this operation.");
    }

    // Продолжает именно сохранённый аварийный план после подтверждённого Promote.
    public async Task RecoverEmergencyAsync(PostgreSqlSwitchPlan plan, Action<string> progress, CancellationToken ct, bool rebuild = false)
    {
        if (!plan.Emergency)
            throw new InvalidOperationException("The retained plan is not an emergency switch.");

        var fence = await ReadConfirmedPhaseAsync(remote, plan, "Fence", plan.From, ct);
        var proof = fence.Fence ?? throw new IOException("The saved emergency Fence has no proof.");
        CheckFenceProof(plan, proof);
        await ReadConfirmedPhaseAsync(remote, plan, "Promote", plan.To, ct);

        // Повтор с тем же идентификатором читает уже принятую или завершённую фазу.
        await RunPhaseAsync(plan, rebuild ? "Rebuild" : "Rejoin", plan.From, proof, progress, ct);
        await remote.PlanAsync(plan.From, "complete", plan, ct);
        await remote.PlanAsync(plan.To, "complete", plan, ct);
        progress($"COMPLETED: Primary={plan.To}; Standby={plan.From}.");
    }

    public static async Task<PostgreSqlRemoteOperation> ReadConfirmedPhaseAsync(IPostgreSqlSwitchRemote remote, PostgreSqlSwitchPlan plan, string mode, string node, CancellationToken ct)
    {
        var operation = await remote.ReadAsync(node, mode, plan, ct);
        EnsureConfirmed(plan, mode, operation);
        return operation;
    }

    // Подтверждает один из двух способов восстановления на прежнем Primary.
    internal static async Task VerifyEmergencyRecoveryAsync(IPostgreSqlSwitchRemote remote, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        var rejoin = await remote.ReadAsync(plan.From, "Rejoin", plan, ct);
        if (rejoin.State == "Succeeded")
        {
            EnsureConfirmed(plan, "Rejoin", rejoin);
            return;
        }

        await ReadConfirmedPhaseAsync(remote, plan, "Rebuild", plan.From, ct);
    }

    // Аварийное повышение оставляет резервирование до отдельного Rejoin.
    public async Task RunAsync(PostgreSqlSwitchPlan plan, Action<string> progress, CancellationToken ct)
    {
        foreach (var address in new[] { plan.From, plan.To }.Order(StringComparer.Ordinal))
            await remote.PlanAsync(address, "reserve", plan, ct);

        var fence = await RunPhaseAsync(plan, "Fence", plan.From, null, progress, ct);
        await RunPhaseAsync(plan, "Promote", plan.To, fence.Fence, progress, ct);

        if (plan.Emergency)
        {
            progress($"PROMOTED: Primary={plan.To}; {plan.From} remains fenced pending recovery.");
            return;
        }

        await RunPhaseAsync(plan, "Rejoin", plan.From, fence.Fence, progress, ct);

        foreach (var address in new[] { plan.From, plan.To }.Order(StringComparer.Ordinal))
            await remote.PlanAsync(address, "complete", plan, ct);

        progress($"COMPLETED: Primary={plan.To}; Standby={plan.From}.");
    }

    private async Task<PostgreSqlRemoteOperation> RunPhaseAsync(PostgreSqlSwitchPlan plan, string mode, string address, JsonElement? proof, Action<string> progress, CancellationToken ct)
    {
        progress($"{mode}: request on {address}");

        PostgreSqlRemoteOperation operation;
        try
        {
            operation = await remote.SubmitAsync(address, mode, new(plan, proof), ct);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex, ct))
        {
            // Запрос мог попасть в очередь до потери ответа. Читаем ту же операцию.
            operation = await ReadPhaseAsync(address, mode, plan, ct);
            if (operation.State == "Missing")
                throw;
        }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        while (operation.State is "Queued" or "Running")
        {
            if (mode != "Rebuild" && DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Phase completion is not confirmed. Resume the same plan; the child script was not cancelled.");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            operation = await ReadPhaseAsync(address, mode, plan, ct);
        }

        EnsureConfirmed(plan, mode, operation);

        if (mode == "Fence")
            CheckFenceProof(plan, operation.Fence ?? throw new IOException("Fence succeeded without its exchange file."));

        progress($"{mode}: confirmed on {address}");
        return operation;
    }

    private async Task<PostgreSqlRemoteOperation> ReadPhaseAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await remote.ReadAsync(address, mode, plan, ct);
            }
            catch (Exception ex) when (retry < 2 && IsCommunicationFailure(ex, ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static bool IsCommunicationFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is (HttpRequestException or IOException or OperationCanceledException);

    private static void EnsureConfirmed(PostgreSqlSwitchPlan plan, string mode, PostgreSqlRemoteOperation operation)
    {
        if (operation.State != "Succeeded" || operation.Id != plan.OperationId(mode))
            throw new InvalidOperationException($"{mode}: {operation.State}. {operation.Message} Reservation retained; subsequent phases were not requested.");
    }
}
