using Microsoft.Extensions.Options;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Хранит короткую in-memory историю Calc -> SCADA writes.
///
/// История предназначена для оперативной диагностики.
/// PostgreSQL audit для неё сейчас не создаём.
/// </summary>
internal sealed class CalcWriteDiagnosticsRegistry(IOptionsMonitor<CalcWriteOptions> options)
{
    private const int MaximumHistoryCount = 100;

    private readonly object _sync = new();
    private readonly Queue<CalcWriteAttemptDto> _recentAttempts = [];

    private long _attemptCount;
    private long _successCount;
    private long _errorCount;
    private DateTimeOffset? _lastAttemptAtUtc;

    public void Record(CalcWriteAttemptDto attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_sync)
        {
            _attemptCount++;

            if (attempt.Status == CalcWriteAttemptStatusDto.Success)
                _successCount++;
            else
                _errorCount++;

            _lastAttemptAtUtc = attempt.AttemptedAtUtc;

            _recentAttempts.Enqueue(attempt);

            while (_recentAttempts.Count > MaximumHistoryCount)
                _recentAttempts.Dequeue();
        }
    }

    public CalcWriteDiagnosticsDto GetSnapshot()
    {
        lock (_sync)
        {
            return new CalcWriteDiagnosticsDto
            {
                Enabled = options.CurrentValue.Enabled,
                AttemptCount = _attemptCount,
                SuccessCount = _successCount,
                ErrorCount = _errorCount,
                LastAttemptAtUtc = _lastAttemptAtUtc,
                RecentAttempts = _recentAttempts.Reverse().Select(Clone).ToList()
            };
        }
    }

    private static CalcWriteAttemptDto Clone(CalcWriteAttemptDto source)
    {
        return new CalcWriteAttemptDto
        {
            AttemptedAtUtc = source.AttemptedAtUtc,
            JobId = source.JobId,
            JobName = source.JobName,
            OutputKey = source.OutputKey,
            TagName = source.TagName,
            RawValue = source.RawValue,
            Scale = source.Scale,
            Offset = source.Offset,
            WrittenValue = source.WrittenValue,
            Status = source.Status,
            ErrorMessage = source.ErrorMessage
        };
    }
}