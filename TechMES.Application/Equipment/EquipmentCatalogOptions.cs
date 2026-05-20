namespace TechMES.Application.Equipment;

/// <summary>
/// Настройки каталога оборудования.
/// 
/// Важно:
/// ScadaAllowedTypes — это реальные типы из Plant SCADA.
/// TypeAliases — маппинг этих SCADA-типов в WEB-группы.
/// </summary>
public sealed class EquipmentCatalogOptions
{
    /// <summary>
    /// Источник списка оборудования:
    /// - InMemory
    /// - CtApi
    /// </summary>
    public string Provider { get; set; } = "InMemory";

    /// <summary>
    /// Таблица Plant SCADA, из которой читаем оборудование.
    /// Обычно EQUIP.
    /// </summary>
    public string CtApiTableName { get; set; } = "EQUIP";

    /// <summary>
    /// Ручной фильтр для CtApi Find.
    /// 
    /// Если задан — используем его как есть.
    /// Если пустой — provider сам построит фильтр по ScadaAllowedTypes.
    /// </summary>
    public string CtApiFilter { get; set; } = "";

    /// <summary>
    /// Cluster для CtApi Find.
    /// Если пусто — используется default/active cluster.
    /// </summary>
    public string CtApiCluster { get; set; } = "";

    /// <summary>
    /// Поле имени оборудования.
    /// </summary>
    public string NameField { get; set; } = "NAME";

    /// <summary>
    /// Поле реального SCADA-типа оборудования.
    /// 
    /// В WPF это поле давало значения:
    /// DigitalIn, Motor, ValveA, AnalogIn и т.д.
    /// </summary>
    public string ScadaTypeField { get; set; } = "TYPE";

    /// <summary>
    /// Возможные поля, где может лежать SCADA-тип.
    /// Если ScadaTypeField не даст значения, provider попробует эти поля.
    /// </summary>
    public string[] ScadaTypeFieldCandidates { get; set; } =
    [
        "TYPE",
        "EQUIPTYPE",
        "EQUIP_TYPE",
        "TYPEGROUP",
        "TYPE_GROUP"
    ];

    /// <summary>
    /// Поле комментария/описания.
    /// </summary>
    public string CommentField { get; set; } = "COMMENT";

    /// <summary>
    /// Поле parent/group.
    /// Пока может быть пустым.
    /// </summary>
    public string ParentField { get; set; } = "";

    /// <summary>
    /// Строить ли CtApi Find filter по ScadaAllowedTypes.
    /// 
    /// true:
    ///     Runtime.Service будет запрашивать из CtApi только нужные SCADA-типы.
    /// 
    /// false:
    ///     Runtime.Service прочитает все строки и отфильтрует их уже в C#.
    /// </summary>
    public bool UseCtApiTypeFilter { get; set; } = true;

    /// <summary>
    /// Реальные типы оборудования из Plant SCADA, которые нам нужны.
    /// 
    /// Это не ComboBox-группы.
    /// Это значения, по которым фильтруем EQUIP в CtApi.
    /// </summary>
    public string[] ScadaAllowedTypes { get; set; } =
    [
        "Equipment",
        "DigitalIn",
        "DigitalInSiemens",
        "DigitalOut",
        "DigitalOutSiemens",
        "Motor",
        "MotorSiemens",
        "AnalogIn",
        "AnalogInSiemens",
        "AnalogInCalc",
        "AnalogInCalcSiemens",
        "ValveA",
        "ValveASiemens",
        "ValveA_EL",
        "ValveD",
        "ValveDSiemens",
        "Atv",
        "AtvSiemens"
    ];

    /// <summary>
    /// Маппинг SCADA-типов в WEB-группы.
    /// 
    /// Ключ — тип из Plant SCADA.
    /// Значение — EquipmentTypeGroup, который отображается в WEB ComboBox.
    /// </summary>
    public Dictionary<string, string> TypeAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}