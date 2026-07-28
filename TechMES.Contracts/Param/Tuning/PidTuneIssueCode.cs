namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Стабильный код причины, по которой автоматическая идентификация процесса
/// не смогла рассчитать PID-настройки по выбранной области тренда.
/// </summary>
public enum PidTuneIssueCode
{
    /// <summary>
    /// Ошибка отсутствует.
    /// </summary>
    None,

    /// <summary>
    /// В выбранной области отсутствуют точки PV или OUT.
    /// </summary>
    MissingTrendData,

    /// <summary>
    /// После сопоставления PV и OUT по времени осталось слишком мало общих точек.
    /// </summary>
    InsufficientAlignedSamples,

    /// <summary>
    /// Ступень OUT находится у границы выбранной области.
    /// </summary>
    StepTooCloseToBoundary,

    /// <summary>
    /// В OUT не найдено выраженное ступенчатое изменение.
    /// </summary>
    NoOutStep,

    /// <summary>
    /// OUT вернулся к исходному уровню и не сохранил ступень.
    /// </summary>
    OutStepNotSustained,

    /// <summary>
    /// OUT изменялся плавно вместо ступенчатого скачка.
    /// </summary>
    OutRampInsteadOfStep,

    /// <summary>
    /// OUT не установился на новом уровне.
    /// </summary>
    OutNotSettled,

    /// <summary>
    /// PV практически не отреагировал на изменение OUT.
    /// </summary>
    PvNoResponse,

    /// <summary>
    /// PV не успел установиться к правой границе выбранной области.
    /// </summary>
    PvNotSettled,

    /// <summary>
    /// Рассчитанное усиление процесса слишком мало.
    /// </summary>
    ProcessGainTooSmall,

    /// <summary>
    /// PV не достиг уровня 28,3 процента полного изменения.
    /// </summary>
    PvDidNotReach28Percent,

    /// <summary>
    /// PV не достиг уровня 63,2 процента полного изменения.
    /// </summary>
    PvDidNotReach63Percent,

    /// <summary>
    /// Точки 28,3 и 63,2 процента расположены в некорректном порядке.
    /// </summary>
    InvalidResponseCrossings,

    /// <summary>
    /// По найденному отклику невозможно получить корректные Tau и Theta.
    /// </summary>
    InvalidModelParameters,

    /// <summary>
    /// Формулы настройки вернули некорректные коэффициенты регулятора.
    /// </summary>
    InvalidControllerParameters,

    /// <summary>
    /// Исторические данные Tune не удалось загрузить.
    /// </summary>
    HistoryLoadFailed,

    /// <summary>
    /// Причина не была классифицирована.
    /// </summary>
    Unknown
}
