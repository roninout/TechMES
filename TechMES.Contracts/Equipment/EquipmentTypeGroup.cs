namespace TechMES.Contracts.Equipment;

/// <summary>
/// Группа/тип оборудования.
/// 
/// Это WEB-аналог TypeGroup из WPF-проекта.
/// Позже значения будут приходить из CtApi/EquipmentService.
/// </summary>
public enum EquipmentTypeGroup
{
    Unknown = 0,

    AI = 1,
    DI = 2,
    DO = 3,

    Motor = 10,
    ATV = 11,

    VGA = 20,
    VGD = 21,
    VGA_EL = 22,

    Equipment = 100
}