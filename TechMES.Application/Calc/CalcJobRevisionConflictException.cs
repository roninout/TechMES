namespace TechMES.Application.Calc;

/// <summary>
/// Возникает, когда задание было изменено другим пользователем
/// после загрузки редактором Maintenance или WEB.
/// </summary>
public sealed class CalcJobRevisionConflictException : Exception
{
    public CalcJobRevisionConflictException(long jobId, long expectedRevision, long currentRevision)
        : base($"Calculation job {jobId} was changed by another user. Expected revision: {expectedRevision}; current revision: {currentRevision}.")
    {
        JobId = jobId;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
    }

    /// <summary>
    /// Идентификатор конфликтующего задания.
    /// </summary>
    public long JobId { get; }

    /// <summary>
    /// Revision, с которой редактор пытался сохранить данные.
    /// </summary>
    public long ExpectedRevision { get; }

    /// <summary>
    /// Текущая Revision в PostgreSQL.
    /// </summary>
    public long CurrentRevision { get; }
}