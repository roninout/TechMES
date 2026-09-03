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
    /// Расчётное Content-оборудование.
    /// В обычное дерево Equipment не добавляется; используется только общим Param write-flow после проверки Calc Catalog.
    /// </summary>
    Content = 30,

    /// <summary>
    /// Расчётное Density-оборудование.
    /// Используется только общим Param write-flow для разрешённых ITEM из Calc Catalog.
    /// </summary>
    Density = 31,

    /// <summary>
    /// Расчётное Capacity-оборудование.
    /// Используется только общим Param write-flow для разрешённых ITEM из Calc Catalog.
    /// </summary>
    Capacity = 32,

    /// <summary>
    /// Групповой узел Equipment.
    /// </summary>
    Equipment = 100
}