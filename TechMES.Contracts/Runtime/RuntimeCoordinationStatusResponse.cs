namespace TechMES.Contracts.Runtime;

/// <summary>
/// Диагностика автоматического выбора владельца.
/// Секреты подключения сюда не попадают.
/// </summary>
public sealed record RuntimeCoordinationStatusResponse
{
    public bool Enabled { get; init; }
    public string Role { get; init; } = "Standalone";
    public string? OwnerNodeId { get; init; }
    public string? OwnerInstanceId { get; init; }
    public Guid? LeaseId { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string Message { get; init; } = "Automatic coordination is disabled.";
}