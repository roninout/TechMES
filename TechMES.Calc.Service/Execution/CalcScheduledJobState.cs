using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Execution;

/// <summary>
/// Хранит runtime-состояние расписания одного задания.
///
/// Объект существует только в памяти Calc.Service и не является
/// сохранённым диагностическим состоянием PostgreSQL.
/// </summary>
internal sealed class CalcScheduledJobState
{
    public CalcScheduledJobState(CalcExecutionJobDto job, DateTimeOffset firstDueUtc)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
        NextDueUtc = firstDueUtc;
    }

    /// <summary>
    /// Текущая конфигурация задания из принятого snapshot.
    /// </summary>
    public CalcExecutionJobDto Job { get; private set; }

    /// <summary>
    /// Ближайшее время запуска задания.
    /// </summary>
    public DateTimeOffset NextDueUtc { get; private set; }

    /// <summary>
    /// Количество завершённых попыток запуска.
    /// </summary>
    public long CompletedAttempts { get; private set; }

    /// <summary>
    /// Номер следующего цикла.
    /// </summary>
    public long NextCycleNumber => CompletedAttempts + 1;

    /// <summary>
    /// Проверяет, наступило ли время запуска.
    /// </summary>
    public bool IsDue(DateTimeOffset nowUtc)
    {
        return NextDueUtc <= nowUtc;
    }

    /// <summary>
    /// Проверяет, соответствует ли состояние той же Revision задания.
    ///
    /// Изменение Revision создаёт новое расписание с немедленным запуском.
    /// </summary>
    public bool Matches(CalcExecutionJobDto job)
    {
        return Job.Id == job.Id
            && Job.Revision == job.Revision
            && string.Equals(Job.DefinitionCode, job.DefinitionCode, StringComparison.Ordinal)
            && string.Equals(Job.DefinitionVersion, job.DefinitionVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Обновляет DTO без сброса существующего расписания.
    /// </summary>
    public void Refresh(CalcExecutionJobDto job)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>
    /// Планирует следующий запуск без накопления параллельных циклов.
    ///
    /// Если служба отстала от расписания, пропущенные периоды не выполняются
    /// подряд. Следующий запуск переносится на ближайший будущий период.
    /// </summary>
    public void MarkAttemptCompleted(DateTimeOffset completedAtUtc)
    {
        CompletedAttempts++;

        var period = TimeSpan.FromMilliseconds(Job.PeriodMs);
        var nextDueUtc = NextDueUtc + period;

        if (nextDueUtc <= completedAtUtc)
        {
            var behind = completedAtUtc - nextDueUtc;
            var skippedPeriods = behind.Ticks / period.Ticks + 1;
            nextDueUtc = nextDueUtc.AddTicks(period.Ticks * skippedPeriods);
        }

        NextDueUtc = nextDueUtc;
    }
}