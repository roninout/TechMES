namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Стабильный код причины, по которой автоматическая идентификация процесса
/// не смогла рассчитать параметры выбранной модели.
/// </summary>
public enum PidTuneIssueCode
{
    /// <summary>
    /// Ошибка отсутствует.
    /// </summary>
    None,

    /// <summary>
    /// В выбранной области отсутствуют обязательные точки тренда.
    /// </summary>
    MissingTrendData,

    /// <summary>
    /// После сопоставления трендов по времени осталось слишком мало общих точек.
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
    /// Оставлено для обратной совместимости со старым SimcPidTuner.
    /// </summary>
    OutNotSettled,

    /// <summary>
    /// PV практически не отреагировал на изменение OUT.
    /// </summary>
    PvNoResponse,

    /// <summary>
    /// PV не успел установиться к правой границе выбранной области.
    /// Оставлено для обратной совместимости со старым SimcPidTuner.
    /// </summary>
    PvNotSettled,

    /// <summary>
    /// Рассчитанное усиление процесса слишком мало.
    /// </summary>
    ProcessGainTooSmall,

    /// <summary>
    /// PV не достиг уровня 28,3 процента полного изменения.
    /// Оставлено для обратной совместимости со старым SimcPidTuner.
    /// </summary>
    PvDidNotReach28Percent,

    /// <summary>
    /// PV не достиг уровня 63,2 процента полного изменения.
    /// Оставлено для обратной совместимости со старым SimcPidTuner.
    /// </summary>
    PvDidNotReach63Percent,

    /// <summary>
    /// Точки 28,3 и 63,2 процента расположены в некорректном порядке.
    /// Оставлено для обратной совместимости со старым SimcPidTuner.
    /// </summary>
    InvalidResponseCrossings,

    /// <summary>
    /// По выбранным данным невозможно получить корректные параметры модели.
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
    /// Выбранная математическая модель плохо описывает фактический тренд.
    /// </summary>
    PoorModelFit,

    /// <summary>
    /// Для интегрирующего процесса не найдено устойчивое изменение наклона PV.
    /// </summary>
    IntegratingSlopeNotDetected,

    /// <summary>
    /// Test Kp отсутствует, не читается как число или не является положительным.
    /// </summary>
    ClosedLoopTestKpInvalid,

    /// <summary>
    /// В PV не найдены выраженные периодические колебания.
    /// </summary>
    ClosedLoopNoOscillation,

    /// <summary>
    /// Для оценки ClosedLoop найдено слишком мало полных циклов.
    /// </summary>
    ClosedLoopInsufficientCycles,

    /// <summary>
    /// Период найденных ClosedLoop-колебаний нестабилен.
    /// </summary>
    ClosedLoopPeriodUnstable,

    /// <summary>
    /// Амплитуда найденных ClosedLoop-колебаний сильно изменяется.
    /// </summary>
    ClosedLoopAmplitudeUnstable,

    /// <summary>
    /// Колебания ClosedLoop затухают: Test Kp ещё ниже критического Ku.
    /// </summary>
    ClosedLoopOscillationsDamped,

    /// <summary>
    /// Колебания ClosedLoop растут: Test Kp выше критического Ku.
    /// </summary>
    ClosedLoopOscillationsGrowing,

    /// <summary>
    /// Причина не была классифицирована.
    /// </summary>
    Unknown
}
