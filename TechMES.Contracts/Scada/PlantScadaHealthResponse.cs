namespace TechMES.Contracts.Scada;

/// <summary>
/// Health-ответ Plant SCADA adapter-а.
/// 
/// Runtime.Service отдаёт этот contract в WEB/Configurator,
/// чтобы было видно, подключён ли CtApi и какой provider используется.
/// </summary>
public sealed class PlantScadaHealthResponse
{
    public string Provider { get; set; } = "";

    public PlantScadaConnectionStatus Status { get; set; } = PlantScadaConnectionStatus.Unknown;

    public bool IsConnected { get; set; }

    public string Message { get; set; } = "";

    public DateTime Time { get; set; } = DateTime.Now;
}