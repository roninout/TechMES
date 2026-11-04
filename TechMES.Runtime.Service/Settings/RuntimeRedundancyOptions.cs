namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Постоянные параметры узла. Рабочий mode назначает координатор.
/// Отдельного Health Period здесь нет.
/// </summary>
public sealed class RuntimeRedundancyOptions
{
    public bool Enabled { get; set; }
    public string Scope { get; set; } = "TechMES.Runtime";
    public string Role { get; set; } = "Primary";
    public int FailureThreshold { get; set; } = 3;
    public int RecoveryThreshold { get; set; } = 3;
}