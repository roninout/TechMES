using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Потокобезопасно хранит последний execution lease,
/// подтверждённый Runtime.Service.
/// </summary>
internal sealed class CalcServiceLeaseState
{
    private readonly object _sync = new();

    private bool _isOwner;
    private string _leaseEpoch = "";
    private long _leaseToken;
    private DateTimeOffset _validUntilUtc = DateTimeOffset.MinValue;

    public void Apply(CalcServiceHeartbeatResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (_sync)
        {
            if (!response.IsLeaseOwner || string.IsNullOrWhiteSpace(response.LeaseEpoch) || response.LeaseToken <= 0)
            {
                ClearCore();
                return;
            }

            // Локальное право заканчивается немного раньше server-side lease.
            var remainingMs = Math.Max(0L, response.LeaseRemainingMilliseconds - 500L);

            _isOwner = remainingMs > 0;
            _leaseEpoch = _isOwner ? response.LeaseEpoch.Trim() : "";
            _leaseToken = _isOwner ? response.LeaseToken : 0;
            _validUntilUtc = _isOwner
                ? DateTimeOffset.UtcNow.AddMilliseconds(remainingMs)
                : DateTimeOffset.MinValue;
        }
    }

    public CalcLocalLeaseSnapshot GetSnapshot(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var valid = _isOwner && _leaseEpoch.Length > 0 && _leaseToken > 0 && nowUtc < _validUntilUtc;

            return valid
                ? new CalcLocalLeaseSnapshot(true, _leaseEpoch, _leaseToken, _validUntilUtc)
                : CalcLocalLeaseSnapshot.None;
        }
    }

    public bool IsCurrentOwner(string leaseEpoch, long leaseToken, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            return _isOwner
                && _leaseToken == leaseToken
                && string.Equals(_leaseEpoch, leaseEpoch, StringComparison.Ordinal)
                && nowUtc < _validUntilUtc;
        }
    }

    private void ClearCore()
    {
        _isOwner = false;
        _leaseEpoch = "";
        _leaseToken = 0;
        _validUntilUtc = DateTimeOffset.MinValue;
    }
}

internal readonly record struct CalcLocalLeaseSnapshot(bool IsOwner, string LeaseEpoch, long LeaseToken, DateTimeOffset ValidUntilUtc)
{
    public static CalcLocalLeaseSnapshot None => new(false, "", 0, DateTimeOffset.MinValue);
}