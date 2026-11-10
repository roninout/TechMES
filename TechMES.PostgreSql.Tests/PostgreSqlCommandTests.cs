using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.PostgreSql.Control;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlCommandTests
{
    // Завершённая операция не выполняется повторно после создания нового исполнителя.
    [Fact]
    public async Task CompletedOperationIsNotExecutedAgainAfterRestart()
    {
        using var scenario = new Scenario();

        var directory = scenario.Enqueue("Fence");

        await scenario.Worker().RunOnceAsync(CancellationToken.None);
        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, scenario.Runner.Calls);
        Assert.Equal("Succeeded", scenario.Journal(directory).State);
    }

    // Незавершённый запуск переживает рестарт и блокирует новые команды.
    [Fact]
    public async Task InterruptedOperationBlocksEveryNewCommand()
    {
        using var scenario = new Scenario();

        var first = scenario.Enqueue("Fence");

        PostgreSqlCommandStore.Write(Path.Combine(first, "journal.json"), new PostgreSqlCommandJournal(Path.GetFileName(first), "unknown", "Running", DateTimeOffset.UtcNow, "Started"));

        scenario.Enqueue("Fence");

        await scenario.Worker().RunOnceAsync(CancellationToken.None);
        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal("NeedsReview", scenario.Journal(first).State);
        Assert.Equal(0, scenario.Runner.Calls);
    }

    // Пропавший результат, чужой Id, код ошибки и сбой связи запрещают повтор.
    [Theory]
    [InlineData("Missing")]
    [InlineData("WrongId")]
    [InlineData("Failed")]
    [InlineData("Throw")]
    public async Task UnconfirmedExecutionIsNotRetried(string behavior)
    {
        using var scenario = new Scenario();

        scenario.Runner.Behavior = behavior;

        var directory = scenario.Enqueue("Fence");

        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        scenario.Enqueue("Fence");

        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, scenario.Runner.Calls);
        Assert.Equal("NeedsReview", scenario.Journal(directory).State);
    }

    // Некорректные запросы отвергаются до запуска процесса.
    [Theory]
    [InlineData("Expired")]
    [InlineData("Cluster")]
    [InlineData("Direction")]
    [InlineData("Proof")]
    public async Task InvalidRequestDoesNotStartTheScript(string invalid)
    {
        using var scenario = new Scenario();

        var directory = scenario.Enqueue("Fence");
        var path = Path.Combine(directory, "request.json");
        var request = PostgreSqlCommandStore.Read<PostgreSqlCommand>(path);

        request = invalid switch
        {
            "Expired" => request with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11) },
            "Cluster" => request with { SystemIdentifier = "123" },
            "Direction" => request with { To = request.From },
            _ => request with { Mode = "Promote" }
        };

        PostgreSqlCommandStore.Write(path, request);

        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Runner.Calls);
        Assert.Equal("Rejected", scenario.Journal(directory).State);
    }

    // Изменение старого запроса не превращает тот же OperationId в новую команду.
    [Fact]
    public async Task ChangingCompletedRequestBlocksTheQueue()
    {
        using var scenario = new Scenario();

        var directory = scenario.Enqueue("Fence");
        var worker = scenario.Worker();

        await worker.RunOnceAsync(CancellationToken.None);

        var path = Path.Combine(directory, "request.json");

        PostgreSqlCommandStore.Write(path, PostgreSqlCommandStore.Read<PostgreSqlCommand>(path) with { To = "changed" });

        scenario.Enqueue("Fence");

        await Assert.ThrowsAsync<IOException>(() => worker.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, scenario.Runner.Calls);
    }

    // Два одновременных опроса не создают второй процесс.
    [Fact]
    public async Task ConcurrentPollingDoesNotDuplicateExecution()
    {
        using var scenario = new Scenario();

        scenario.Runner.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Enqueue("Fence");

        var worker = scenario.Worker();
        var first = worker.RunOnceAsync(CancellationToken.None);

        await scenario.Runner.Started.Task;

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, scenario.Runner.Calls);

        scenario.Runner.Hold.SetResult();

        await first;
    }

    // Отмена ожидания после принятия команды не прерывает мутацию.
    [Fact]
    public async Task CancellationAfterAcceptanceDoesNotAbortTheMutation()
    {
        using var scenario = new Scenario();
        using var cancellation = new CancellationTokenSource();

        scenario.Runner.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var directory = scenario.Enqueue("Fence");
        var execution = scenario.Worker().RunOnceAsync(cancellation.Token);

        await scenario.Runner.Started.Task;

        cancellation.Cancel();
        scenario.Runner.Hold.SetResult();

        await execution;

        Assert.Equal("Succeeded", scenario.Journal(directory).State);
    }

    // Артефакты прежнего запуска без журнала требуют разбора.
    [Fact]
    public async Task UnjournaledArtifactsBlockFurtherExecution()
    {
        using var scenario = new Scenario();

        var directory = scenario.Enqueue("Fence");

        File.WriteAllText(Path.Combine(directory, "script-result.json"), "{}");

        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal("NeedsReview", scenario.Journal(directory).State);
        Assert.Equal(0, scenario.Runner.Calls);
    }

    // Отключённые команды не запускают дочерний скрипт.
    [Fact]
    public async Task DisabledCommandsDoNotExecute()
    {
        using var scenario = new Scenario();

        scenario.Enqueue("Fence");

        await scenario.Worker(false).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Runner.Calls);
    }

    // Promote и Rejoin импортируют подтверждение и проверяют собственный конечный этап.
    [Theory]
    [InlineData("Promote")]
    [InlineData("Rejoin")]
    public async Task ReturningPhasesImportTheFenceProof(string mode)
    {
        using var scenario = new Scenario();

        var directory = scenario.Enqueue(mode);
        var path = Path.Combine(directory, "request.json");
        var proof = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["State"] = "Fenced", ["Sid"] = scenario.Settings.ExpectedSystemIdentifier });

        PostgreSqlCommandStore.Write(path, PostgreSqlCommandStore.Read<PostgreSqlCommand>(path) with { Fence = proof });

        await scenario.Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal("Succeeded", scenario.Journal(directory).State);
        Assert.Equal("Fenced", PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(directory, "fence.json")).GetProperty("State").GetString());
    }

    private sealed class Scenario : IDisposable, IPostgreSqlActionHost
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public PostgreSqlControlOptions Settings { get; } = new() { CommandsEnabled = true, ExpectedSystemIdentifier = "7703468038317098096" };
        public PostgreSqlCommandStore Store { get; }
        public Runner Runner { get; } = new();

        // Использует атомарный журнал без запуска настоящих служб и PostgreSQL.
        public Scenario()
        {
            Store = new(Options.Create(Settings), new PostgreSqlControllerLease(_root));

            Store.Initialize();

            File.WriteAllBytes(Store.CredentialPath, [1]);
        }

        // Создаёт новый исполнитель, сохраняя прежний каталог.
        public PostgreSqlCommandWorker Worker(bool enabled = true)
        {
            return new(Options.Create(Settings with { CommandsEnabled = enabled }), this, Store, Runner, NullLogger<PostgreSqlCommandWorker>.Instance);
        }

        // Исключает из теста настоящий SCM; проверки очереди остаются реальными.
        public Task ExecuteOwnedAsync(Func<PostgreSqlControlOptions, Task> action, CancellationToken ct)
        {
            return action(Settings);
        }

        // Публикует запрос тем же форматом, который использует административный клиент.
        public string Enqueue(string mode)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Store.Root, id);

            Directory.CreateDirectory(directory);

            PostgreSqlCommandStore.Write(Path.Combine(directory, "request.json"), new PostgreSqlCommand(id, mode, "192.168.1.131", "192.168.1.132", Settings.ExpectedSystemIdentifier, DateTimeOffset.UtcNow, null));

            return directory;
        }

        // Читает сохранённый результат без зависимости от памяти исполнителя.
        public PostgreSqlCommandJournal Journal(string directory)
        {
            return PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(Path.Combine(directory, "journal.json"));
        }

        // Удаляет только каталог данного теста.
        public void Dispose()
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class Runner : IPostgreSqlSwitchRunner
    {
        public int Calls;
        public string Behavior = "Success";
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;

        // Моделирует подтверждение, сбой или пропавший результат после записи Running.
        public async Task<int> RunAsync(PostgreSqlControlOptions settings, PostgreSqlCommand request, string directory, string credentialPath)
        {
            Calls++;

            Assert.Equal("Running", PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(Path.Combine(directory, "journal.json")).State);

            Started.TrySetResult();

            if (Hold is not null)
                await Hold.Task;

            if (Behavior == "Throw")
                throw new IOException("Simulated process communication failure.");

            if (Behavior != "Missing")
            {
                PostgreSqlCommandStore.Write(Path.Combine(directory, "script-result.json"), new Dictionary<string, object> { ["OperationId"] = Behavior == "WrongId" ? "wrong" : request.Id, ["ExpectedSystemIdentifier"] = settings.ExpectedSystemIdentifier, ["Mode"] = request.Mode, ["From"] = request.From, ["To"] = request.To, ["Success"] = Behavior != "Failed", ["Data"] = new Dictionary<string, object> { ["State"] = request.Mode == "Fence" ? "Fenced" : request.Mode == "Promote" ? "Promoted" : "Rejoined" } });
            }

            return Behavior == "Failed" ? 1 : 0;
        }
    }
}