namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Стабильный код причины, по которой автоматическая идентификация процесса
/// не смогла рассчитать параметры выбранной модели.
/// </summary>
public enum PidTuneIssueCode
{
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
    /// OUT формально сохранил новый средний уровень, но хвост продолжает
    /// заметно колебаться/дрейфовать относительно величины DeltaOUT.
    /// </summary>
    OutNotSettled,

    /// <summary>
    /// PV практически не отреагировал на изменение OUT.
    /// </summary>
    PvNoResponse,

    /// <summary>
    /// Legacy-код старого SimcPidTuner: PV не успел установиться.
    /// </summary>
    PvNotSettled,

    /// <summary>
    /// Рассчитанное усиление процесса слишком мало.
    /// </summary>
    ProcessGainTooSmall,

    /// <summary>
    /// Legacy-код старого двухточечного FOPDT-алгоритма.
    /// </summary>
    PvDidNotReach28Percent,

    /// <summary>
    /// Legacy-код старого двухточечного FOPDT-алгоритма.
    /// </summary>
    PvDidNotReach63Percent,

    /// <summary>
    /// Legacy-код старого двухточечного FOPDT-алгоритма.
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
    /// FOPDT fit формально найден, но выбранное окно показывает меньше
    /// приблизительно одной Tau фактического отклика после Theta.
    /// K/Tau в таком случае плохо идентифицируемы.
    /// </summary>
    FopdtResponseWindowTooShort,

    /// <summary>
    /// Для интегрирующего процесса не найдено устойчивое изменение наклона PV.
    /// </summary>
    IntegratingSlopeNotDetected,

    /// <summary>
    /// Test Kp отсутствует, не читается как число или не является положительным.
    /// </summary>
    ClosedLoopTestKpInvalid,

    /// <summary>
    /// В ошибке PV-SP не найдены выраженные периодические колебания.
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
    /// Колебания ClosedLoop затухают: текущий Test Kp ниже критического Ku.
    /// </summary>
    ClosedLoopOscillationsDamped,

    /// <summary>
    /// Колебания ClosedLoop растут: текущий Test Kp выше критического Ku.
    /// </summary>
    ClosedLoopOscillationsGrowing,

    /// <summary>
    /// SP в выбранном ClosedLoop-окне заметно изменяется или дрейфует.
    /// Такой участок нельзя использовать как ultimate-gain test:
    /// колебания PV могут быть вызваны самим заданием.
    /// </summary>
    ClosedLoopSetpointUnstable,

    /// <summary>
    /// Причина не была классифицирована.
    /// </summary>
    Unknown
}
