namespace TechMES.Contracts.Runtime;

/// <summary>
/// Диагностика Main DB без строк подключения и паролей.
/// </summary>
public sealed record MainDatabaseHealthResponse
{
    public bool Enabled { get; init; }
    public int HealthPeriodSeconds { get; init; } = 10;
    public string? ActiveRole { get; init; }
    public MainDatabaseWriteSafety WriteSafety { get; init; } = new();
    public MainDatabaseServerState Primary { get; init; } = new() { Role = "Primary" };
    public MainDatabaseServerState Secondary { get; init; } = new() { Role = "Secondary" };
}

/// <summary>Запрет рабочих действий при обнаруженном противоречии физического кластера.</summary>
public sealed record MainDatabaseWriteSafety
{
    public bool Enabled { get; init; }
    public bool WritesBlocked { get; init; }
    public string Code { get; init; } = "NotRequired";
    public string Message { get; init; } = "Cluster write protection is not required.";
    public int RecoveryChecks { get; init; }
    public int RequiredRecoveryChecks { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
}

public sealed record MainDatabaseServerState
{
    public string Role { get; init; } = "";
    public bool Configured { get; init; }
    public bool Connected { get; init; }
    public bool CanWrite { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string Message { get; init; } = "Not checked.";
}
