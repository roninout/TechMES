namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Единая точка входа автоматической идентификации процесса.
/// WEB передает выбранную пользователем модель и видимые точки тренда.
/// </summary>
public static class PidProcessIdentifier
{
    /// <summary>
    /// Совместимая перегрузка для прежних FOPDT/Integrating вызовов.
    ///
    /// Для ClosedLoop новая логика требует SP, поэтому старый вызов без SP
    /// вернет MissingTrendData вместо анализа одного PV.
    /// </summary>
    public static PidProcessIdentificationResult Identify(
        string processModel,
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample>? output = null,
        double? testKp = null)
    {
        return Identify(
            processModel,
            pv,
            output,
            sp: null,
            testKp: testKp);
    }

    /// <summary>
    /// Полная точка входа.
    ///
    /// FOPDT:
    ///     PV + OUT.
    ///
    /// Integrating:
    ///     PV + OUT.
    ///
    /// ClosedLoop:
    ///     PV + SP + current online Test Kp.
    /// </summary>
    public static PidProcessIdentificationResult Identify(
        string processModel,
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample>? output,
        IReadOnlyList<PidTuningSample>? sp,
        double? testKp)
    {
        return processModel switch
        {
            PidTuneProcessModel.Fopdt =>
                FopdtProcessIdentifier.Identify(
                    pv,
                    output ?? []),

            PidTuneProcessModel.Integrating =>
                IntegratingProcessIdentifier.Identify(
                    pv,
                    output ?? []),

            PidTuneProcessModel.ClosedLoop =>
                ClosedLoopOscillationIdentifier.Identify(
                    pv,
                    sp ?? [],
                    testKp),

            _ =>
                PidProcessIdentificationResult.Fail(
                    processModel ?? "",
                    PidTuneIssueCode.InvalidModelParameters,
                    $"Unsupported process model: {processModel}.")
        };
    }
}
