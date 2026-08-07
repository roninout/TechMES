namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Результат автоматической идентификации модели процесса по видимому участку тренда.
/// Один DTO используется для FOPDT, Integrating и ClosedLoop.
/// Поля, которые не относятся к выбранной модели, остаются null.
/// </summary>
public sealed class PidProcessIdentificationResult
{
    public bool IsSuccess { get; init; }

    public string ProcessModel { get; init; } = "";

    public PidTuneIssueCode IssueCode { get; init; }

    public string ErrorMessage { get; init; } = "";

    public double? K { get; init; }

    public double? Tau { get; init; }

    public double? Theta { get; init; }

    public double? TauC { get; init; }

    public double? Ki { get; init; }

    public double? Ku { get; init; }

    public double? Tu { get; init; }

    /// <summary>
    /// Найденная величина ступени OUT = OUTafter - OUTbefore.
    /// </summary>
    public double? DeltaOut { get; init; }

    /// <summary>
    /// Исходный уровень PV перед ступенью FOPDT.
    /// </summary>
    public double? PvBaseline { get; init; }

    /// <summary>
    /// Полная fitted-амплитуда FOPDT A = K * DeltaOUT.
    /// </summary>
    public double? ResponseAmplitude { get; init; }

    /// <summary>
    /// Исходный наклон PV до реакции Integrating-модели.
    /// </summary>
    public double? BaseSlope { get; init; }

    /// <summary>
    /// Изменение наклона PV после Theta в Integrating-модели.
    /// Ki = SlopeChange / DeltaOUT.
    /// </summary>
    public double? SlopeChange { get; init; }

    /// <summary>
    /// Среднеквадратичная ошибка аппроксимации в единицах PV.
    /// Для ClosedLoop не используется.
    /// </summary>
    public double? Rmse { get; init; }

    /// <summary>
    /// Коэффициент детерминации аппроксимации.
    /// Для ClosedLoop не используется.
    /// </summary>
    public double? R2 { get; init; }

    /// <summary>
    /// Для FOPDT: доля полного fitted-отклика, реально наблюдаемая
    /// к правой границе выбранного окна:
    /// 1 - exp(-(Tobs-Theta)/Tau).
    /// </summary>
    public double? ObservedResponseFraction { get; init; }

    /// <summary>
    /// Робастный разброс хвоста OUT (P95-P05), деленный на |DeltaOUT|.
    /// Используется FOPDT/Integrating.
    /// </summary>
    public double? OutputTailRangeRatio { get; init; }

    /// <summary>
    /// Средняя амплитуда устойчивых колебаний detrended ошибки PV-SP.
    /// </summary>
    public double? OscillationAmplitude { get; init; }

    /// <summary>
    /// Коэффициент вариации периодов ClosedLoop.
    /// </summary>
    public double? PeriodCv { get; init; }

    /// <summary>
    /// Коэффициент вариации амплитуд ClosedLoop.
    /// </summary>
    public double? AmplitudeCv { get; init; }

    /// <summary>
    /// Отношение средней амплитуды последних циклов к первым.
    /// Значение около 1 означает незатухающие колебания.
    /// </summary>
    public double? AmplitudeTrendRatio { get; init; }

    /// <summary>
    /// Робастный разброс SP P95-P05 относительно peak-to-peak амплитуды ошибки PV-SP.
    /// </summary>
    public double? SetpointVariationRatio { get; init; }

    /// <summary>
    /// Полный линейный дрейф SP за окно относительно peak-to-peak амплитуды ошибки PV-SP.
    /// </summary>
    public double? SetpointDriftRatio { get; init; }

    /// <summary>
    /// Количество полных циклов, использованных в ClosedLoop-проверке амплитуды.
    /// </summary>
    public int? CyclesUsed { get; init; }

    public double DtSeconds { get; init; }

    public int PointsUsed { get; init; }

    public DateTime? StepTimeUtc { get; init; }

    public static PidProcessIdentificationResult Fail(
        string processModel,
        PidTuneIssueCode issueCode,
        string message,
        int pointsUsed = 0,
        double dtSeconds = 0)
    {
        return new PidProcessIdentificationResult
        {
            IsSuccess = false,
            ProcessModel = processModel,
            IssueCode = issueCode,
            ErrorMessage = message,
            PointsUsed = pointsUsed,
            DtSeconds = dtSeconds
        };
    }
}
