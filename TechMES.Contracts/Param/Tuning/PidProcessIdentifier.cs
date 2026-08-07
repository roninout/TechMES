namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Единая точка входа автоматической идентификации процесса.
/// WEB передает выбранную пользователем модель и видимые точки тренда.
/// </summary>
public static class PidProcessIdentifier
{
    public static PidProcessIdentificationResult Identify(
        string processModel,
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample>? output = null,
        double? testKp = null)
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
                    testKp),

            _ => PidProcessIdentificationResult.Fail(
                processModel ?? "",
                PidTuneIssueCode.InvalidModelParameters,
                $"Unsupported process model: {processModel}.")
        };
    }
}
