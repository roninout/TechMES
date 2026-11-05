namespace TechMES.Contracts.Runtime;

public sealed record PostgreSqlClusterHealthResponse
{
    public bool Enabled { get; init; }
    public int HealthPeriodSeconds { get; init; }
    public string Topology { get; init; } = "NotChecked";
    public PostgreSqlServerState Primary { get; init; } = new() { Role = "Primary" };
    public PostgreSqlServerState Secondary { get; init; } = new() { Role = "Secondary" };
}

public sealed record PostgreSqlServerState
{
    // Role — имя поля в настройках; ActualRole — фактическая роль PostgreSQL.
    public string Role { get; init; } = "";
    public bool Configured { get; init; }
    public bool Connected { get; init; }
    public string? ActualRole { get; init; }
    public bool CanWrite { get; init; }
    public string? ServerAddress { get; init; }
    public int? ServerPort { get; init; }
    public string? SystemIdentifier { get; init; }
    public bool MonitoringOk { get; init; }
    public string? CurrentWalLsn { get; init; }
    public string? ReceivedWalLsn { get; init; }
    public string? ReplayedWalLsn { get; init; }
    public bool? ReplayPaused { get; init; }
    public string? ReceiverStatus { get; init; }
    public string? SenderHost { get; init; }
    public string? SlotName { get; init; }
    public DateTimeOffset? LastMessageReceivedAtUtc { get; init; }
    public int? StreamingReplicaCount { get; init; }
    public int ConsecutiveSuccesses { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string Message { get; init; } = "Not checked.";
}