namespace TechMES.Calc.Service.Execution;

/// <summary>
/// Хранит последний результат каждого активного Job для использования
/// входами CalculationOutput.
///
/// Ключ Revision предотвращает использование результата старой
/// конфигурации после изменения source Job.
/// </summary>
internal sealed class CalcDependencyOutputCache
{
    private readonly object _sync = new();
    private readonly Dictionary<long, CalcDependencyOutputSnapshot> _values = [];

    public void Store(CalcJobExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            _values[result.JobId] = new CalcDependencyOutputSnapshot(
                result.Revision,
                result.Status,
                result.CompletedAtUtc,
                new Dictionary<string, double>(result.Outputs, StringComparer.OrdinalIgnoreCase));
        }
    }

    public bool TryGet(long jobId, long revision, out CalcDependencyOutputSnapshot? snapshot)
    {
        lock (_sync)
        {
            if (_values.TryGetValue(jobId, out var value) && value.Revision == revision)
            {
                snapshot = value;
                return true;
            }
        }

        snapshot = null;
        return false;
    }

    public void Prune(IEnumerable<long> activeJobIds)
    {
        var active = activeJobIds.ToHashSet();

        lock (_sync)
        {
            foreach (var jobId in _values.Keys.Where(jobId => !active.Contains(jobId)).ToList())
                _values.Remove(jobId);
        }
    }
}

internal sealed record CalcDependencyOutputSnapshot(long Revision, CalcJobExecutionStatus Status, DateTimeOffset CompletedAtUtc, IReadOnlyDictionary<string, double> Outputs);