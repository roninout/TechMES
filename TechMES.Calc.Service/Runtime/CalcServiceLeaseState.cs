using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// Потокобезопасно хранит последний lease, подтверждённый Runtime.
///
/// CalcWorker не выполняет Jobs, если локальный lease уже истёк,
/// даже если последний configuration snapshot всё ещё находится в памяти.
/// </summary>
internal sealed class CalcServiceLeaseState
{
    private readonly object _sync = new();

    private bool _isOwner;
    private long _leaseToken;
    private DateTimeOffset _validUntilUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Применяет ответ Runtime на heartbeat.
    /// </summary>
    public void Apply(CalcServiceHeartbeatResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (_sync)
        {
            if (!response.IsLeaseOwner || response.LeaseToken <= 0)
            {
                ClearCore();
                return;
            }

            /*
             * Оставляем небольшой safety margin.
             * Даже при задержке сети локальное право закончится
             * немного раньше серверного lease, но никогда намеренно позже.
             */
            var remainingMs = Math.Max(0L, response.LeaseRemainingMilliseconds - 500L);

            _isOwner = remainingMs > 0;
            _leaseToken = _isOwner ? response.LeaseToken : 0;
            _validUntilUtc = _isOwner
                ? DateTimeOffset.UtcNow.AddMilliseconds(remainingMs)
                : DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Возвращает текущий локальный lease snapshot.
    /// </summary>
    public CalcLocalLeaseSnapshot GetSnapshot(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var valid = _isOwner && _leaseToken > 0 && nowUtc < _validUntilUtc;

            return valid
                ? new CalcLocalLeaseSnapshot(true, _leaseToken, _validUntilUtc)
                : new CalcLocalLeaseSnapshot(false, 0, DateTimeOffset.MinValue);
        }
    }

    public bool IsCurrentOwner(long leaseToken, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            return _isOwner
                && _leaseToken == leaseToken
                && nowUtc < _validUntilUtc;
        }
    }

    private void ClearCore()
    {
        _isOwner = false;
        _leaseToken = 0;
        _validUntilUtc = DateTimeOffset.MinValue;
    }
}

/// <summary>
/// Неизменяемый snapshot локального ownership.
/// </summary>
internal readonly record struct CalcLocalLeaseSnapshot(bool IsOwner, long LeaseToken, DateTimeOffset ValidUntilUtc);