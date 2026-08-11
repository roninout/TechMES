namespace TechMES.Runtime.Service.Settings;

/// <summary>
/// Глобальное разрешение автоматической записи результатов Calc в SCADA.
///
/// Даже если Job и Output разрешены для записи,
/// при Enabled=false Runtime никогда не вызывает TagWrite.
/// </summary>
public sealed class CalcWriteOptions
{
    public const string SectionName = "CalcWrites";

    public bool Enabled { get; set; }
}