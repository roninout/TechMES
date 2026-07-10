namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Набор ручных параметров, которые нужны выбранной модели процесса.
/// Для FOPDT используются K/Tau/Theta/TauC, для интегрирующего процесса - Ki/Theta/TauC,
/// для закрытого контура - Ku/Tu.
/// </summary>
public sealed class PidTuneCalculationRequest
{
    public string ProcessModel { get; init; } = PidTuneProcessModel.Fopdt;

    public string TuneMethod { get; init; } = PidTuneMethod.FopdtSimcPid;

    public double? FopdtK { get; init; }

    public double? FopdtTau { get; init; }

    public double? FopdtTheta { get; init; }

    public double? FopdtTauC { get; init; }

    public double? IntegratingKi { get; init; }

    public double? IntegratingTheta { get; init; }

    public double? IntegratingTauC { get; init; }

    public double? ClosedLoopKu { get; init; }

    public double? ClosedLoopTu { get; init; }
}
