namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Поддерживаемые модели процесса для вкладки PID Tune.
/// Значения используются в WEB-комбобоксах и в общем калькуляторе настроек.
/// </summary>
public static class PidTuneProcessModel
{
    public const string Fopdt = "FOPDT";

    public const string Integrating = "Integrating";

    public const string ClosedLoop = "ClosedLoop";
}
