namespace TechMES.Calc.Service.Execution;

/// <summary>
/// Хранит последний результат каждого активного Job для использования
/// входами CalculationOutput.
///
/// Cache действует только внутри текущей принятой конфигурации.
/// При изменении configuration snapshot полностью очищается, чтобы
/// downstream Job не мог использовать результат, рассчитанный по старой
/// версии upstream-конфигурации.
/// </summary>
internal sealed class CalcDependencyOutputCache
{
    private readonly object _sync = new();
    private readonly Dictionary<long, CalcDependencyOutputSnapshot> _values = [];

    /// <summary>
    /// Сохраняет последний результат Job.
    ///
    /// Сохраняются также Skipped/Error, чтобы dependent Job не продолжал
    /// использовать предыдущий Success после неуспешного нового запуска source.
    /// </summary>
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

    /// <summary>
    /// Возвращает результат только для текущей Revision source Job.
    /// </summary>
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

    /// <summary>
    /// Удаляет результаты Jobs, которых больше нет в активном graph.
    /// </summary>
    public void Prune(IEnumerable<long> activeJobIds)
    {
        var active = activeJobIds.ToHashSet();

        lock (_sync)
        {
            foreach (var jobId in _values.Keys.Where(jobId => !active.Contains(jobId)).ToList())
                _values.Remove(jobId);
        }
    }

    /// <summary>
    /// Полностью инвалидирует результаты предыдущего configuration snapshot.
    ///
    /// Это необходимо не только при изменении самого source Job,
    /// но и для транзитивных цепочек A -> B -> C.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
            _values.Clear();
    }
}

/// <summary>
/// Последнее фактическое выполнение одного Job.
/// </summary>
internal sealed record CalcDependencyOutputSnapshot(
    long Revision,
    CalcJobExecutionStatus Status,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyDictionary<string, double> Outputs);