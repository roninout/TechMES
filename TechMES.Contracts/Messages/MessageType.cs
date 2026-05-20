namespace TechMES.Contracts.Messages;

/// <summary>
/// Тип сообщения, который используется и в WEB-интерфейсе, и в Runtime Service.
///
/// Этот enum лежит в Contracts, потому что обе стороны должны одинаково понимать
/// допустимые значения типа сообщения.
/// </summary>
public enum MessageType
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
