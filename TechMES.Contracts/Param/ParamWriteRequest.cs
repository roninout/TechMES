namespace TechMES.Contracts.Param;

/// <summary>
/// Запрос WEB -> Runtime.Service на запись одного Param item.
/// Runtime.Service сам разрешает оборудование, проверяет allow-list и выполняет TagWrite через CtApi.
/// </summary>
public sealed class ParamWriteRequest
{
    /// <summary>
    /// Имя EquipItem, который пользователь хочет изменить.
    /// </summary>
    public string ItemName { get; set; } = "";

    /// <summary>
    /// Новое значение в строковом виде. Backend нормализует boolean и number перед записью.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Комментарий оператора. Используется в audit Cicode SaveActionOperators.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Имя клиента/устройства. Если WEB не передал Actor, Runtime.Service подставляет DeviceName.
    /// </summary>
    public string? Actor { get; set; }
}
