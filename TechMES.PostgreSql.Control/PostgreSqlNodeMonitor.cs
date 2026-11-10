using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public enum PostgreSqlHostKind
{
    Runtime,
    Standalone
}

public interface IPostgreSqlServicePresence
{
    /// <summary>Проверяет наличие регистрации Windows-службы.</summary>
    bool IsInstalled(string serviceName);
}

public interface IPostgreSqlLocalProbe
{
    /// <summary>Читает локальную службу, процесс и контрольный файл PostgreSQL.</summary>
    Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct);
}

public interface IPostgreSqlControllerLease
{
    /// <summary>Возвращает путь снимка для указанной службы БД.</summary>
    string SnapshotPath(string serviceName);

    /// <summary>Получает исключительное владение либо сообщает о другом владельце.</summary>
    IDisposable? TryAcquire(string serviceName);
}

public interface IPostgreSqlActionHost
{
    /// <summary>Проверяет локального владельца и выполняет одну команду без отмены после её принятия.</summary>
    Task ExecuteOwnedAsync(Func<PostgreSqlControlOptions, Task> action, CancellationToken ct);
}

/// <summary>Один экземпляр модуля внутри Runtime либо отдельного хоста на компьютере БД.</summary>
public sealed class PostgreSqlNodeMonitor : IHostedService, IPostgreSqlActionHost, IDisposable
{
    private readonly PostgreSqlControlOptions _settings;
    private readonly PostgreSqlHostKind _hostKind;
    private readonly int _period;
    private readonly IPostgreSqlServicePresence _presence;
    private readonly IPostgreSqlLocalProbe _probe;
    private readonly IPostgreSqlControllerLease _leases;
    private readonly ILogger<PostgreSqlNodeMonitor> _logger;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private IDisposable? _lease;
    private PostgreSqlLocalHealthResponse _state;
    private volatile bool _stopping;

    /// <summary>В Runtime использует существующий Health Period; отдельный хост использует локальную настройку.</summary>
    public PostgreSqlNodeMonitor(IOptions<PostgreSqlControlOptions> settings, IConfiguration configuration, PostgreSqlHostKind hostKind, IPostgreSqlServicePresence presence, IPostgreSqlLocalProbe probe, IPostgreSqlControllerLease leases, ILogger<PostgreSqlNodeMonitor> logger)
    {
        _settings = settings.Value;
        _hostKind = hostKind;
        _period = Math.Clamp(hostKind == PostgreSqlHostKind.Runtime ? configuration.GetValue<int?>("CtApi:HealthCheckPeriodSeconds") ?? 10 : _settings.HealthPeriodSeconds, 5, 3600);
        _presence = presence;
        _probe = probe;
        _leases = leases;
        _logger = logger;
        _state = new() { Settings = _settings, HostKind = hostKind.ToString(), HealthPeriodSeconds = _period };
    }

    /// <summary>Возвращает снимок без SQL, SCM и чтения файлов.</summary>
    public PostgreSqlLocalHealthResponse GetHealth()
    {
        return Volatile.Read(ref _state);
    }

    /// <summary>Регистрация модуля не задерживает запуск HTTP и подготовку Runtime.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>Выбирает единственный локальный хост и обновляет данные независимо от Runtime Active/Standby.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct))
            return;

        try
        {
            if (_stopping)
                return;

            if (_hostKind == PostgreSqlHostKind.Standalone && _presence.IsInstalled("TechMES.Runtime.Service"))
            {
                ReleaseLease();
                Publish(new() { ControllerState = "UseRuntime", Message = "TechMES.Runtime.Service is installed. Local PostgreSQL monitoring belongs to Runtime." });
                return;
            }

            var installed = _presence.IsInstalled(_settings.ServiceName);

            if (!_settings.Enabled && !_settings.AutoDetect)
            {
                ReleaseLease();
                Publish(new() { ControllerState = "Disabled", Message = "Local PostgreSQL monitoring is disabled." });
                return;
            }

            if (!installed)
            {
                ReleaseLease();
                Publish(new() { ControllerState = "NotInstalled", Message = "The configured local PostgreSQL Windows service is not installed." });
                return;
            }

            _lease ??= _leases.TryAcquire(_settings.ServiceName);

            if (_lease is null)
            {
                Publish(new() { ControllerState = "AnotherHost", MonitoringEnabled = true, Message = "Another TechMES process owns this local PostgreSQL controller." });
                return;
            }

            var checkedState = await _probe.CheckAsync(_settings, Math.Clamp(_period - 1, 3, 15), ct);

            ct.ThrowIfCancellationRequested();

            var snapshot = _leases.SnapshotPath(_settings.ServiceName);
            var next = checkedState with { ControllerState = checkedState.PermissionsOk && checkedState.Service is not null && checkedState.PostgreSqlRole is not null ? "Monitoring" : "NotReady", MonitoringEnabled = true, OwnsController = true, SnapshotPath = snapshot };

            Publish(next);

            // Снимок отдельного хоста читается локально без дополнительных проверок.
            await SaveSnapshotAsync(snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Publish(new() { ControllerState = "Failed", OwnsController = _lease is not null, Message = $"Local controller check failed ({ex.GetType().Name})." });
            _logger.LogWarning("PostgreSQL local controller check failed. ErrorType={ErrorType}.", ex.GetType().Name);
        }
        finally
        {
            if (_stopping)
                ReleaseLease();

            _refresh.Release();
        }
    }

    /// <summary>Не связывает мутацию с HTTP, остановкой таймера или сроком ожидания вызывающего кода.</summary>
    public async Task ExecuteOwnedAsync(Func<PostgreSqlControlOptions, Task> action, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);

        try
        {
            if (_stopping || !_settings.CommandsEnabled || !System.Text.RegularExpressions.Regex.IsMatch(_settings.ExpectedSystemIdentifier, "^[0-9]{1,20}$"))
                throw new InvalidOperationException("Local PostgreSQL commands are disabled or the cluster identity is not configured.");

            if (_lease is null || (_hostKind == PostgreSqlHostKind.Standalone && _presence.IsInstalled("TechMES.Runtime.Service")))
                throw new InvalidOperationException("This process does not own the local PostgreSQL controller.");

            var state = await _probe.CheckAsync(_settings, 15, ct);
            var service = state.Service;
            var stopped = service is not null && service.ServiceState == "Stopped" && service.PostmasterProcessId == 0 && service.ListenerProcessId == 0;

            if (!state.PermissionsOk || service is null || (!service.ManagedByService && !stopped) || state.SystemIdentifier != _settings.ExpectedSystemIdentifier)
                throw new InvalidOperationException("Local PostgreSQL service ownership, permissions or cluster identity were not confirmed.");

            ct.ThrowIfCancellationRequested();

            await action(_settings);
        }
        finally
        {
            if (_stopping)
                ReleaseLease();

            _refresh.Release();
        }
    }

    /// <summary>Заменяет файл снимка атомарно, пока хост удерживает межпроцессное владение.</summary>
    private async Task SaveSnapshotAsync(string path, CancellationToken ct)
    {
        var temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(GetHealth(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);

        File.Move(temporary, path, true);
    }

    /// <summary>Атомарно публикует наблюдение и записывает в журнал только смену состояния.</summary>
    private void Publish(PostgreSqlLocalHealthResponse result)
    {
        var previous = GetHealth();
        var next = result with { Settings = _settings, HostKind = _hostKind.ToString(), HealthPeriodSeconds = _period, CheckedAtUtc = DateTimeOffset.UtcNow };

        Volatile.Write(ref _state, next);

        if (previous.ControllerState != next.ControllerState || previous.PostgreSqlRole != next.PostgreSqlRole)
            _logger.LogInformation("PostgreSQL local controller: Host={Host}, State={State}, Role={Role}.", next.HostKind, next.ControllerState, next.PostgreSqlRole ?? "Unknown");
    }

    /// <summary>Освобождает владение после текущей проверки, без остановки PostgreSQL.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;

        await _refresh.WaitAsync(ct);

        try
        {
            if (_lease is not null)
            {
                var path = _leases.SnapshotPath(_settings.ServiceName);

                Publish(GetHealth() with { ControllerState = "Stopped", MonitoringEnabled = false, OwnsController = false, Message = "The local PostgreSQL host is stopping." });

                await SaveSnapshotAsync(path, ct);
            }
        }
        finally
        {
            ReleaseLease();
            _refresh.Release();
        }
    }

    /// <summary>Закрывает межпроцессный дескриптор без привязки к потоку.</summary>
    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>Окончательно освобождает ресурсы модуля.</summary>
    public void Dispose()
    {
        _stopping = true;

        if (!_refresh.Wait(0))
            return; // Выполняющаяся команда освободит дескриптор в своём finally.

        try
        {
            ReleaseLease();
        }
        finally
        {
            _refresh.Release();
        }
    }
}

public static class PostgreSqlNodeServices
{
    /// <summary>Одинаковая регистрация для Runtime и отдельной Windows-службы.</summary>
    public static IServiceCollection AddPostgreSqlNode(this IServiceCollection services, IConfiguration configuration, PostgreSqlHostKind hostKind)
    {
        services.AddOptions<PostgreSqlControlOptions>().Bind(configuration.GetSection(PostgreSqlControlOptions.SectionName));

        services.TryAddSingleton<IPostgreSqlServicePresence, WindowsPostgreSqlServicePresence>();
        services.TryAddSingleton<IPostgreSqlControllerLease, PostgreSqlControllerLease>();
        services.TryAddSingleton<IPostgreSqlLocalProbe, PostgreSqlLocalProbe>();

        services.AddSingleton(provider => new PostgreSqlNodeMonitor(provider.GetRequiredService<IOptions<PostgreSqlControlOptions>>(), configuration, hostKind, provider.GetRequiredService<IPostgreSqlServicePresence>(), provider.GetRequiredService<IPostgreSqlLocalProbe>(), provider.GetRequiredService<IPostgreSqlControllerLease>(), provider.GetRequiredService<ILogger<PostgreSqlNodeMonitor>>()));

        services.AddHostedService(provider => provider.GetRequiredService<PostgreSqlNodeMonitor>());

        services.AddSingleton<IPostgreSqlActionHost>(provider => provider.GetRequiredService<PostgreSqlNodeMonitor>());
        services.TryAddSingleton<PostgreSqlCommandStore>();
        services.TryAddSingleton<IPostgreSqlSwitchRunner, PostgreSqlSwitchRunner>();

        services.AddHostedService<PostgreSqlCommandWorker>();

        if (hostKind == PostgreSqlHostKind.Standalone)
            services.AddHostedService<PostgreSqlNodePollingWorker>();

        return services;
    }
}

/// <summary>В Runtime проверки запускает PlantScadaHealthWorker; этот таймер нужен отдельному хосту.</summary>
public sealed class PostgreSqlNodePollingWorker(PostgreSqlNodeMonitor monitor) : BackgroundService
{
    /// <summary>Следит за локальной БД и возможным появлением Runtime на том же компьютере.</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(monitor.GetHealth().HealthPeriodSeconds));

        try
        {
            do
            {
                await monitor.RefreshAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}