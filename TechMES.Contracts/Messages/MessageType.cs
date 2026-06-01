namespace TechMES.Contracts.Messages;

/// <summary>
/// Тип сообщения, который используется и в WEB-интерфейсе, и в Runtime Service.
///
/// Этот enum лежит в Contracts, потому что обе стороны должны одинаково понимать
/// допустимые значения типа сообщения.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// Информационное сообщение.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Предупреждение.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Критичное сообщение.
    /// </summary>
    Critical = 2
}
