namespace TechMES.Contracts.Runtime;

/// <summary>
/// Состояние самого Runtime без обращения к PostgreSQL, CtApi и ODBC.
/// </summary>
public sealed record RuntimeActivityStatusResponse
{
    public string NodeId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string Mode { get; init; } = "Starting";
    public bool CanExecuteCommands { get; init; }
    public bool StandbyRequested { get; init; }
    public int PendingOperations { get; init; }
    public string? ActivationId { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset ChangedAtUtc { get; init; }
}