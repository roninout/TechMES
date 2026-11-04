namespace TechMES.Application.Runtime;

/// <summary>
/// Общее разрешение на активность Runtime.
/// SQL и строки подключения остаются в infrastructure.
/// </summary>
public interface IRuntimeLeadershipStore
{
    Task<RuntimeLeadershipSnapshot> TouchAsync(RuntimeLeadershipRequest request, CancellationToken ct);

    Task ReleaseAsync(string scope, string instanceId, Guid leaseId, CancellationToken ct);
}

public sealed record RuntimeLeadershipRequest(string Scope, string NodeId, string InstanceId, string Role, bool CanAcquire, bool AdvertisePrimary, int LeaseSeconds, int PrimaryFreshSeconds, int RecoveryThreshold);

public sealed record RuntimeLeadershipSnapshot(bool IsOwner, bool YieldToPrimary, Guid? LeaseId, string? OwnerNodeId, string? OwnerInstanceId, string? OwnerRole, DateTimeOffset? ExpiresAtUtc);