using System.Net;

namespace TechMES.Web.Clients;

/// <summary>
/// Ошибка, которую Runtime.Service вернул при работе с Calc API.
///
/// Помимо текста содержит HTTP status, стабильный backend-код
/// и сведения о конфликте Revision.
/// </summary>
public sealed class CalcApiClientException : Exception
{
    public CalcApiClientException(HttpStatusCode statusCode, string? errorCode, string message,
        long? jobId = null, long? expectedRevision = null, long? currentRevision = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        JobId = jobId;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
    public long? JobId { get; }
    public long? ExpectedRevision { get; }
    public long? CurrentRevision { get; }
}