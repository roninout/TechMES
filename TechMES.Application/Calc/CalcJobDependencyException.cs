namespace TechMES.Application.Calc;

/// <summary>
/// Возникает при попытке удалить расчётное задание,
/// результат которого используется другим заданием.
/// </summary>
public sealed class CalcJobDependencyException : Exception
{
    public CalcJobDependencyException(long jobId, string message, Exception? innerException = null) : base(message, innerException)
    {
        JobId = jobId;
    }

    /// <summary>
    /// Идентификатор задания, которое не удалось удалить.
    /// </summary>
    public long JobId { get; }
}