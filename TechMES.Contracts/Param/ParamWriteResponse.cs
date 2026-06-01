using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Результат попытки записи Param item.
/// Возвращается и при успехе, и при отказе, чтобы WEB мог показать оператору точную причину.
/// </summary>
public sealed class ParamWriteResponse
{
    /// <summary>
    /// Оборудование, для которого выполнялась запись.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Нормализованная группа типа оборудования, по которой выбирается allow-list.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Param item, который пытались записать.
    /// </summary>
    public string ItemName { get; set; } = "";

    /// <summary>
    /// Разрешенное имя Plant SCADA tag.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Значение, прочитанное перед записью. Нужно для подтверждения и audit.
    /// </summary>
    public string? CurrentValue { get; set; }

    /// <summary>
    /// Нормализованное значение, которое было бы записано или реально записано.
    /// </summary>
    public string? WrittenValue { get; set; }

    /// <summary>
    /// Общий результат операции. False означает отказ до TagWrite или ошибку CtApi.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// True, если операция прошла в режиме проверки без реального TagWrite.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// True, если после реальной записи backend пытался вызвать SaveActionOperators.
    /// </summary>
    public bool AuditAttempted { get; set; }

    /// <summary>
    /// True, если audit Cicode завершился без исключения.
    /// Ошибка audit не отменяет саму запись.
    /// </summary>
    public bool AuditSucceeded { get; set; }

    /// <summary>
    /// Короткое информационное сообщение для UI.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Причина отказа или ошибка CtApi.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Локальное время формирования ответа Runtime.Service.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;
}
