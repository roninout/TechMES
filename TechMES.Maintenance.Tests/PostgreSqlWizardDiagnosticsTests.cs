using TechMES.Contracts.Runtime;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

public sealed class PostgreSqlWizardDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("different cluster")]
    [InlineData("wrong sender")]
    [InlineData("stale")]
    [InlineData("wrong route")]
    [InlineData("writes blocked")]
    public void ConfirmsOnlyMatchingLiveReplicationAndWriteRoute(string? problem)
    {
        const string primary = "10.0.0.41";
        const string standby = "10.0.0.42";
        const string sid = "1234";
        var now = DateTimeOffset.UtcNow;
        var receiver = new PostgreSqlServerState
        {
            Role = "Primary",
            ServerAddress = standby,
            ActualRole = "Standby",
            SystemIdentifier = problem == "different cluster" ? "5678" : sid,
            CheckedAtUtc = problem == "stale" ? now.AddMinutes(-5) : now,
            ReceiverStatus = "streaming",
            SenderHost = problem == "wrong sender" ? "10.0.0.43" : primary,
            ReplayPaused = false
        };
        var cluster = new PostgreSqlClusterHealthResponse
        {
            Enabled = true,
            Topology = "PreferredSecondary",
            Primary = receiver,
            Secondary = new() { Role = "Secondary", ServerAddress = primary, ActualRole = "Primary", CanWrite = true, SystemIdentifier = sid, CheckedAtUtc = now }
        };
        var main = new MainDatabaseHealthResponse
        {
            Enabled = true,
            ActiveRole = problem == "wrong route" ? "Primary" : "Secondary",
            WriteSafety = new() { WritesBlocked = problem == "writes blocked" },
            Secondary = new() { Connected = true, CanWrite = true, CheckedAtUtc = now }
        };

        Assert.Equal(problem is null, PostgreSqlWizardDiagnostics.Check(cluster, main, primary, standby, sid, now) is null);
    }
}
