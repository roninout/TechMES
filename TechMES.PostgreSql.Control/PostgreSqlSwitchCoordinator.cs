using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TechMES.PostgreSql.Control;

public interface IPostgreSqlSwitchRemote
{
    Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct);
    Task PlanAsync(string address, string action, PostgreSqlSwitchPlan plan, CancellationToken ct);
    Task<PostgreSqlRemoteOperation> SubmitAsync(string address, string mode, PostgreSqlPhaseRequest phase, CancellationToken ct);
    Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct);
}

// Клиент предъявляет свой сертификат и проверяет pin и IP сервера.
public sealed class PostgreSqlSwitchRemote : IPostgreSqlSwitchRemote, IDisposable
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    // Создаёт клиентов только для двух настроенных адресов.
    public PostgreSqlSwitchRemote(PostgreSqlNetworkOptions options, X509Certificate2 certificate)
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

            _clients.Add(address, new HttpClient(handler) { BaseAddress = new Uri($"https://{address}:{options.Port}/"), Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 262144 });
        }
    }

    // Получает состояние узла без запуска мутаций.
    public async Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct)
    {
        return await _clients[address].GetFromJsonAsync<PostgreSqlRemoteStatus>("v1/status", ct) ?? throw new IOException("Empty node status.");
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

// Один вызов выполняет весь плановый цикл через существующие локальные скрипты.
public sealed class PostgreSqlSwitchCoordinator(IPostgreSqlSwitchRemote remote)
{
    // Перед новым планом требует согласованную пару Primary/Standby.
    public static void CheckInitial(PostgreSqlSwitchPlan plan, PostgreSqlRemoteStatus source, PostgreSqlRemoteStatus target)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var node in new[] { source, target })
        {
            var local = node.Local;

            if (local.CheckedAtUtc is null || now - local.CheckedAtUtc > TimeSpan.FromSeconds(Math.Max(60, 2 * local.HealthPeriodSeconds)) || local.CheckedAtUtc > now.AddMinutes(1) || local.ControllerState != "Monitoring" || !local.OwnsController || !local.PermissionsOk || local.Service?.ManagedByService != true || !local.Settings.CommandsEnabled || local.Settings.ExpectedSystemIdentifier != plan.SystemIdentifier || local.SystemIdentifier != plan.SystemIdentifier)
                throw new InvalidOperationException("Both local controllers must confirm fresh service ownership, commands and the expected cluster.");
        }

        if (source.NodeAddress != plan.From || target.NodeAddress != plan.To || source.Local.PostgreSqlRole != "Primary" || target.Local.PostgreSqlRole != "Standby" || source.Local.Timeline != target.Local.Timeline || source.Local.Timeline is null)
            throw new InvalidOperationException("Expected the selected Primary and its Standby in one timeline. Local phase checks will also verify SQL and WAL.");
    }

    // Резервирует оба узла и последовательно ожидает Fence, Promote и Rejoin.
    public async Task RunAsync(PostgreSqlSwitchPlan plan, Action<string> progress, CancellationToken ct)
    {
        foreach (var address in new[] { plan.From, plan.To }.Order(StringComparer.Ordinal))
            await remote.PlanAsync(address, "reserve", plan, ct);

        JsonElement? proof = null;

        foreach (var mode in new[] { "Fence", "Promote", "Rejoin" })
        {
            var address = mode == "Promote" ? plan.To : plan.From;

            progress($"{mode}: request on {address}");

            var operation = await remote.SubmitAsync(address, mode, new(plan, proof), ct);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

            while (operation.State is "Queued" or "Running")
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException("Phase completion is not confirmed. Resume the same plan; the child script was not cancelled.");

                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                operation = await remote.ReadAsync(address, mode, plan, ct);
            }

            if (operation.State != "Succeeded" || operation.Id != plan.OperationId(mode))
                throw new InvalidOperationException($"{mode}: {operation.State}. {operation.Message} Reservation retained; subsequent phases were not requested.");

            if (mode == "Fence")
            {
                proof = operation.Fence ?? throw new IOException("Fence succeeded without its exchange file.");

                var value = proof.Value;

                if (value.GetProperty("State").GetString() != "Fenced" || value.GetProperty("From").GetString() != plan.From || value.GetProperty("To").GetString() != plan.To || value.GetProperty("Sid").GetString() != plan.SystemIdentifier)
                    throw new IOException("The fence proof does not match the selected plan.");
            }

            progress($"{mode}: confirmed on {address}");
        }

        foreach (var address in new[] { plan.From, plan.To }.Order(StringComparer.Ordinal))
            await remote.PlanAsync(address, "complete", plan, ct);

        progress($"COMPLETED: Primary={plan.To}; Standby={plan.From}.");
    }
}