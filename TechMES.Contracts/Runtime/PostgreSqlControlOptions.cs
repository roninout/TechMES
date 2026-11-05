namespace TechMES.Contracts.Runtime;

/// <summary>Параметры локального экземпляра PostgreSQL на компьютере Runtime.</summary>
public sealed record PostgreSqlControlOptions
{
    public const string SectionName = "PostgreSqlControl";

    public bool Enabled { get; init; }
    public string ServiceName { get; init; } = "postgresql-x64-18";
    public string BinDirectory { get; init; } = @"D:\PostgreSQL\18\bin";
    public string DataDirectory { get; init; } = @"D:\PostgreSQL\18\data";
    public int Port { get; init; } = 5432;
}

/// <summary>Готовый снимок прав Runtime и принадлежности процесса PostgreSQL службе.</summary>
public sealed record PostgreSqlLocalHealthResponse
{
    public PostgreSqlControlOptions Settings { get; init; } = new();
    public int HealthPeriodSeconds { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string? RuntimeIdentity { get; init; }
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