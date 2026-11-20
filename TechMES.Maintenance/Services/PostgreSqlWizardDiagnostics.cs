using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using TechMES.Contracts.Runtime;

namespace TechMES.Maintenance.Services;

internal static class PostgreSqlWizardDiagnostics
{
    internal static async Task<(PostgreSqlClusterHealthResponse Cluster, MainDatabaseHealthResponse Main)> ReadAsync(string runtimeUrl)
    {
        if (!Uri.TryCreate(runtimeUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Введите базовый URL Runtime, например http://127.0.0.1:5101.");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 65536 };

        var cluster = client.GetFromJsonAsync<PostgreSqlClusterHealthResponse>("api/database/cluster");
        var main = client.GetFromJsonAsync<MainDatabaseHealthResponse>("api/database/health");
        await Task.WhenAll(cluster, main);
        return (await cluster ?? throw new InvalidDataException("Runtime не вернул диагностику PostgreSQL."),
                await main ?? throw new InvalidDataException("Runtime не вернул диагностику Main DB."));
    }

    internal static string? Check(PostgreSqlClusterHealthResponse cluster, MainDatabaseHealthResponse main, string primaryAddress, string standbyAddress, string systemIdentifier, DateTimeOffset now)
    {
        if (!cluster.Enabled || !main.Enabled)
            return "В выбранном Runtime нужно включить диагностику кластера и Main DB.";

        var nodes = new[] { cluster.Primary, cluster.Secondary };
        var primary = nodes.FirstOrDefault(node => node.ServerAddress == primaryAddress);
        var standby = nodes.FirstOrDefault(node => node.ServerAddress == standbyAddress);
        if (primary is null || standby is null || nodes.Any(node => node.SystemIdentifier != systemIdentifier))
            return "Снимок Runtime относится к другой паре или кластеру PostgreSQL.";

        if (nodes.Any(node => !IsFresh(node.CheckedAtUtc, cluster.HealthPeriodSeconds, now)))
            return "Снимки Runtime устарели. Обновите проверку.";

        if (cluster.Topology is not ("PreferredPrimary" or "PreferredSecondary")
            || primary.ActualRole != "Primary" || !primary.CanWrite
            || standby.ActualRole != "Standby" || standby.CanWrite || standby.SenderHost != primaryAddress
            || standby.ReceiverStatus != "streaming" || standby.ReplayPaused != false)
            return "Runtime пока не подтвердил поток WAL от Primary к выбранной реплике.";

        var route = main.ActiveRole switch { "Primary" => main.Primary, "Secondary" => main.Secondary, _ => null };
        if (main.WriteSafety.WritesBlocked || main.ActiveRole != primary.Role || route is not { Connected: true, CanWrite: true }
            || !IsFresh(route.CheckedAtUtc, main.HealthPeriodSeconds, now))
            return "Main DB пока не подтвердил запись на текущий Primary.";

        return null;
    }

    internal static bool IsFresh(DateTimeOffset? timestamp, int period, DateTimeOffset now) =>
        timestamp is { } time && time <= now.AddMinutes(1) && now - time <= TimeSpan.FromSeconds(Math.Max(30, 3d * period));
}
