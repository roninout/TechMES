namespace TechMES.Contracts.Runtime;

/// <summary>Параметры локального экземпляра PostgreSQL для Runtime либо отдельного хоста.</summary>
public sealed record PostgreSqlControlOptions
{
    public const string SectionName = "PostgreSqlControl";

    public bool Enabled { get; init; }
    public bool AutoDetect { get; init; } = true;
    public string ServiceName { get; init; } = "postgresql-x64-18";
    public string BinDirectory { get; init; } = @"D:\PostgreSQL\18\bin";
    public string DataDirectory { get; init; } = @"D:\PostgreSQL\18\data";
    public int Port { get; init; } = 5432;
    public int HealthPeriodSeconds { get; init; } = 10;
    public bool CommandsEnabled { get; init; }
    public string ExpectedSystemIdentifier { get; init; } = "";
}

/// <summary>Готовый снимок прав хоста, роли БД и принадлежности процесса PostgreSQL службе.</summary>
public sealed record PostgreSqlLocalHealthResponse
{
    public PostgreSqlControlOptions Settings { get; init; } = new();
    public int HealthPeriodSeconds { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string? RuntimeIdentity { get; init; }
    public string HostKind { get; init; } = "Runtime";
    public string ControllerState { get; init; } = "NotChecked";
    public bool MonitoringEnabled { get; init; }
    public bool OwnsController { get; init; }
    public string? SnapshotPath { get; init; }
    public string? PostgreSqlRole { get; init; }
    public string? ClusterState { get; init; }
    public string? SystemIdentifier { get; init; }
    public int? Timeline { get; init; }
    public bool PermissionsOk { get; init; }
    public int? WindowsError { get; init; }
    public PostgreSqlServiceState? Service { get; init; }
    public string Message { get; init; } = "Not checked.";
}

/// <summary>Результат проверки SCM, postmaster.pid и владельца порта.</summary>
public sealed record PostgreSqlServiceState
{
    public string Computer { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string ServiceState { get; init; } = "";
    public int ServiceProcessId { get; init; }
    public string ServiceAccount { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public int Port { get; init; }
    public int PostmasterProcessId { get; init; }
    public int ListenerProcessId { get; init; }
    public bool ManagedByService { get; init; }
}