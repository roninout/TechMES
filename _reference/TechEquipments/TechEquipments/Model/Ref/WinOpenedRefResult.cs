
namespace TechEquipments
{
    /// <summary>
    /// Результат поиска ссылки WinOpened.
    /// RefEquip = значение поля REFEQUIP.
    /// Assoc    = значение поля ASSOC.
    /// RefItem  = значение поля REFITEM.
    /// </summary>
    public sealed record WinOpenedRefResult(string RefEquip, string Assoc, string RefItem);
}