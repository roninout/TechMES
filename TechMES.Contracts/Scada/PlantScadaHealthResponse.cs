namespace TechMES.Contracts.Scada;

/// <summary>
/// Health-ответ Plant SCADA adapter-а.
/// 
/// Runtime.Service отдаёт этот contract в WEB/Configurator,
/// чтобы было видно, подключён ли CtApi и какой provider используется.
/// </summary>
public sealed class PlantScadaHealthResponse
{
    /// <summary>
    /// Название активного провайдера: Disabled, Mock или CtApi.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// Статус соединения.
    /// </summary>
    public PlantScadaConnectionStatus Status { get; set; } = PlantScadaConnectionStatus.Unknown;

    /// <summary>
    /// Упрощенный флаг доступности.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Диагностическое сообщение.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Время формирования ответа.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;
}
