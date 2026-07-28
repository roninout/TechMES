namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Поддерживаемые методы расчета PID/PI-настроек.
/// Имена намеренно стабильные: они хранятся в UI-состоянии и выбираются пользователем.
/// </summary>
public static class PidTuneMethod
{
    /// <summary>
    /// SIMC PI для модели FOPDT. Строковое значение сохранено прежним,
    /// чтобы уже сохраненное состояние UI осталось совместимым.
    /// </summary>
    public const string FopdtSimcPi = "FOPDT_SIMC_PID";

    /// <summary>
    /// Обратная совместимость со старым именем константы.
    /// </summary>
    public const string FopdtSimcPid = "FOPDT_SIMC_PID";

    public const string FopdtZieglerNicholsPid = "FOPDT_ZN_PID";

    public const string FopdtCohenCoonPid = "FOPDT_COHEN_COON_PID";

    public const string FopdtAmigoPid = "FOPDT_AMIGO_PID";

    public const string IntegratingSimcPi = "INT_SIMC_PI";

    public const string IntegratingZieglerNicholsPi = "INT_ZN_PI";

    public const string IntegratingAveragingPi = "INT_AVERAGING_PI";

    public const string ClosedLoopZieglerNicholsPid = "CLOSED_ZN_PID";

    public const string ClosedLoopZieglerNicholsSoftPid = "CLOSED_ZN_SOFT_PID";

    public const string ClosedLoopTyreusLuybenPid = "CLOSED_TYREUS_LUYBEN_PID";
}
