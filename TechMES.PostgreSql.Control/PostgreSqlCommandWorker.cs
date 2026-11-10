using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

/// <summary>Один исполнитель в Runtime и Agent; незавершённый запуск не повторяется автоматически.</summary>
public sealed class PostgreSqlCommandWorker(IOptions<PostgreSqlControlOptions> options, IPostgreSqlActionHost host, PostgreSqlCommandStore store, IPostgreSqlSwitchRunner runner, ILogger<PostgreSqlCommandWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ожидает локальные запросы; SQL-проверки выполняются при принятии команды.</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("PostgreSQL command queue is blocked. ErrorType={ErrorType}.", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Принимает максимум один запрос; ранее начатые операции проверяет до новых.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!options.Value.CommandsEnabled || !Directory.Exists(store.Root) || !await _gate.WaitAsync(0, ct))
            return;

        try
        {
            PostgreSqlCommandStore.AssertPrivate(store.Root, true);

            var pending = new List<string>();

            foreach (var directory in Directory.GetDirectories(store.Root).Order(StringComparer.Ordinal))
            {
                if (!Regex.IsMatch(Path.GetFileName(directory), "^[0-9a-f]{32}$"))
                    continue;

                PostgreSqlCommandStore.AssertPrivate(directory, true);

                var path = Path.Combine(directory, "journal.json");

                if (!File.Exists(path))
                {
                    if (File.Exists(Path.Combine(directory, "request.json")))
                        pending.Add(directory);

                    continue;
                }

                var journal = PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(path);

                if (journal.Id != Path.GetFileName(directory))
                    throw new IOException("Operation journal identity mismatch.");

                if (journal.State == "Running")
                {
                    PostgreSqlCommandStore.Write(path, journal with { State = "NeedsReview", UpdatedAtUtc = DateTimeOffset.UtcNow, Message = "The host restarted before execution was confirmed. The script may still be running. Automatic retries are blocked." });
                    return;
                }

                if (journal.State == "NeedsReview")
                    return;

                if (journal.State is not ("Succeeded" or "Rejected"))
                    throw new IOException("Unknown operation journal state.");

                var original = PostgreSqlCommandStore.Read<PostgreSqlCommand>(Path.Combine(directory, "request.json"));

                if (Fingerprint(original) != journal.Fingerprint)
                    throw new IOException("An existing operation request was changed. Queue blocked.");
            }

            if (pending.Count == 0)
                return;

            await host.ExecuteOwnedAsync(async settings =>
            {
                store.Initialize();

                if (!File.Exists(Path.Combine(pending[0], "journal.json")))
                    await ExecuteAsync(pending[0], settings);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Проверяет идентификатор, направление, срок запроса и отсутствие произвольных команд.</summary>
    private static void Validate(PostgreSqlCommand request, string directory, PostgreSqlControlOptions settings)
    {
        if (request.Id != Path.GetFileName(directory) || request.Mode is not ("Fence" or "Promote" or "Rejoin") || request.SystemIdentifier != settings.ExpectedSystemIdentifier)
            throw new InvalidOperationException("Invalid command identity, mode or PostgreSQL cluster.");

        if (request.From == request.To || request.From is not ("192.168.1.131" or "192.168.1.132") || request.To is not ("192.168.1.131" or "192.168.1.132"))
            throw new InvalidOperationException("Invalid switch direction.");

        var age = DateTimeOffset.UtcNow - request.CreatedAtUtc;

        if (age.TotalMinutes is < -1 or > 10)
            throw new InvalidOperationException("The command request expired or its timestamp is in the future.");

        if (request.Mode == "Fence" ? request.Fence is not null : request.Fence is not { ValueKind: JsonValueKind.Object })
            throw new InvalidOperationException("Fence produces a proof; Promote and Rejoin require that proof.");
    }

    /// <summary>Связывает повторный OperationId с неизменным содержимым запроса.</summary>
    private static string Fingerprint(PostgreSqlCommand request)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, PostgreSqlCommandStore.Json)));
    }

    /// <summary>Сохраняет Running до Process.Start; ошибка после этого требует разбора без повтора.</summary>
    private async Task ExecuteAsync(string directory, PostgreSqlControlOptions settings)
    {
        var path = Path.Combine(directory, "journal.json");
        var request = PostgreSqlCommandStore.Read<PostgreSqlCommand>(Path.Combine(directory, "request.json"));
        var journal = new PostgreSqlCommandJournal(request.Id, Fingerprint(request), "Rejected", DateTimeOffset.UtcNow, "");

        if (new[] { "script-result.json", "fence.json", "process-output.log", "process-error.log" }.Any(name => File.Exists(Path.Combine(directory, name))))
        {
            PostgreSqlCommandStore.Write(path, journal with { State = "NeedsReview", Message = "Unjournaled execution files exist. Inspect the previous run; automatic execution is blocked." });
            return;
        }

        try
        {
            Validate(request, directory, settings);
            store.ValidateCredentials();

            if (request.Fence is { } proof)
                PostgreSqlCommandStore.Write(Path.Combine(directory, "fence.json"), proof);
        }
        catch (Exception ex)
        {
            PostgreSqlCommandStore.Write(path, journal with { Message = $"Request rejected before execution ({ex.GetType().Name})." });
            return;
        }

        journal = journal with { State = "Running", UpdatedAtUtc = DateTimeOffset.UtcNow, Message = "The local switch script was accepted; do not repeat this operation." };

        PostgreSqlCommandStore.Write(path, journal);

        try
        {
            var exitCode = await runner.RunAsync(settings, request, directory, store.CredentialPath);
            var result = PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(directory, "script-result.json"));
            var expected = request.Mode == "Fence" ? "Fenced" : request.Mode == "Promote" ? "Promoted" : "Rejoined";
            var confirmed = exitCode == 0 && result.GetProperty("Success").GetBoolean() && result.GetProperty("OperationId").GetString() == request.Id && result.GetProperty("Mode").GetString() == request.Mode && result.GetProperty("From").GetString() == request.From && result.GetProperty("To").GetString() == request.To && result.GetProperty("ExpectedSystemIdentifier").GetString() == settings.ExpectedSystemIdentifier && result.GetProperty("Data").GetProperty("State").GetString() == expected;

            journal = journal with { State = confirmed ? "Succeeded" : "NeedsReview", UpdatedAtUtc = DateTimeOffset.UtcNow, Message = confirmed ? "The script confirmed completion. See script-result.json and its log." : "Completion was not confirmed. Inspect script-result.json and its log; automatic retries are blocked." };
        }
        catch (Exception ex)
        {
            journal = journal with { State = "NeedsReview", UpdatedAtUtc = DateTimeOffset.UtcNow, Message = $"Execution outcome is uncertain ({ex.GetType().Name}). Inspect process-output.log and process-error.log. The child was not terminated. Automatic retries are blocked." };
        }

        PostgreSqlCommandStore.Write(path, journal);

        logger.LogInformation("PostgreSQL command {OperationId}: {Mode}, {State}.", request.Id, request.Mode, journal.State);
    }
}