namespace TechMES.Contracts.Scada;

/// <summary>
/// Состояние подключения Runtime.Service к Plant SCADA.
/// 
/// WEB не должен знать детали CtApi.
/// Он получает только понятный статус.
/// </summary>
public enum PlantScadaConnectionStatus
{
    /// <summary>
    /// Статус еще не определен.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Adapter выключен или не настроен.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// Подключение выполняется.
    /// </summary>
    Connecting = 2,

    /// <summary>
    /// Подключение активно.
    /// </summary>
    Connected = 3,

    /// <summary>
    /// Подключение потеряно или не удалось.
    /// </summary>
    Disconnected = 4,

    /// <summary>
    /// Adapter работает в mock/test режиме.
    /// </summary>
    Mock = 5
}
