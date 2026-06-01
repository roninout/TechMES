namespace TechMES.Infrastructure.CtApi.Settings;

/// <summary>
/// Настройки CtApi adapter-а.
///
/// Эти настройки читаются из Runtime.Service/appsettings.json.
/// Позже WPF Configurator сможет редактировать эти значения.
/// </summary>
public sealed class CtApiOptions
{
    /// <summary>
    /// Provider для Plant SCADA gateway.
    ///
    /// Поддерживаем:
    /// - Disabled: CtApi выключен;
    /// - Mock: тестовый режим без Plant SCADA;
    /// - CtApi: реальный adapter через CtApi.dll.
    /// </summary>
    public string Provider { get; set; } = "Mock";

    /// <summary>
    /// Путь к папке, где лежат CtApi.dll и зависимости.
    ///
    /// По прошлому WPF-опыту лучше указывать установленный Plant SCADA Bin x64,
    /// например:
    /// D:\AVEVA\AVEVA Plant SCADA\Bin\Bin (x64)
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// IP/host Plant SCADA сервера.
    /// Может быть пустым, если CtApi открывается локально.
    /// </summary>
    public string Server { get; set; } = "";

    /// <summary>
    /// Пользователь Plant SCADA.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Пароль Plant SCADA.
    /// Позже вынесем из appsettings.json.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Период health-check CtApi.
    /// </summary>
    public int HealthCheckPeriodSeconds { get; set; } = 10;

    /// <summary>
    /// Разрешать запись через CtApi.
    ///
    /// Для первого реального подключения лучше держать false.
    /// Тогда ReadTag уже можно тестировать,
    /// а WriteTag будет безопасно запрещён.
    /// </summary>
    public bool AllowWrites { get; set; } = false;

    /// <summary>
    /// Тестовый tag для health-check.
    ///
    /// Если пустой — health monitor будет проверять только факт открытия CtApi.
    /// Если задан — monitor будет периодически выполнять TagRead этого tag-а.
    /// </summary>
    public string HealthCheckTag { get; set; } = "";

    /// <summary>
    /// Максимальное количество параллельных TagRead-вызовов для reference pages Param.
    /// </summary>
    public int TagReadParallelism { get; set; } = 4;
}
