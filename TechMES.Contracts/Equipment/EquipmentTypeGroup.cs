namespace TechMES.Contracts.Equipment;

/// <summary>
/// Группа/тип оборудования.
/// 
/// Это WEB-аналог TypeGroup из WPF-проекта.
/// Позже значения будут приходить из CtApi/EquipmentService.
/// </summary>
public enum EquipmentTypeGroup
{
    /// <summary>
    /// Тип не распознан.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Analog input.
    /// </summary>
    AI = 1,

    /// <summary>
    /// Digital input.
    /// </summary>
    DI = 2,

    /// <summary>
    /// Digital output.
    /// </summary>
    DO = 3,

    /// <summary>
    /// Двигатель.
    /// </summary>
    Motor = 10,

    /// <summary>
    /// Частотный преобразователь.
    /// </summary>
    ATV = 11,

    /// <summary>
    /// Аналоговый клапан.
    /// </summary>
    VGA = 20,

    /// <summary>
    /// Дискретный клапан.
    /// </summary>
    VGD = 21,

    /// <summary>
    /// Электрический аналоговый клапан.
    /// </summary>
    VGA_EL = 22,

    /// <summary>
    /// Групповой узел Equipment.
    /// </summary>
    Equipment = 100
}
