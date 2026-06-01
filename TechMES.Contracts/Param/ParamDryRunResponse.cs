namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ вкладки DryRun.
/// </summary>
public sealed class ParamDryRunResponse
{
    /// <summary>
    /// Имя выбранного оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли DryRun для выбранного оборудования.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Время формирования ответа.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>
    /// Имя связанного DryRun оборудования.
    /// </summary>
    public string? DryRunEquipmentName { get; set; }

    /// <summary>
    /// Compact Param snapshot связанного DryRun оборудования.
    /// </summary>
    public ParamLinkedParamDto? DryRunEquipment { get; set; }

    /// <summary>
    /// Связанный DI, если он найден.
    /// </summary>
    public ParamDiDoRefDto? LinkedDi { get; set; }

    /// <summary>
    /// Связанный AI, если он найден.
    /// </summary>
    public ParamLinkedParamDto? LinkedAi { get; set; }
}
