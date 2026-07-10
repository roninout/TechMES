namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Общий калькулятор PID/PI-настроек для всех методов, доступных во вкладке Tune.
/// UI передает сюда только выбранную модель, метод и численные параметры.
/// </summary>
public static class PidTuneCalculator
{
    /// <summary>
    /// Выполняет расчет по выбранной модели процесса и выбранному методу настройки.
    /// </summary>
    public static PidTuneCalculationResult Calculate(PidTuneCalculationRequest request)
    {
        return request.ProcessModel switch
        {
            PidTuneProcessModel.Integrating => CalculateIntegrating(request),
            PidTuneProcessModel.ClosedLoop => CalculateClosedLoop(request),
            _ => CalculateFopdt(request)
        };
    }

    /// <summary>
    /// Методы для FOPDT-модели: K, Tau, Theta и, для SIMC, желаемая постоянная замкнутого контура TauC.
    /// </summary>
    private static PidTuneCalculationResult CalculateFopdt(PidTuneCalculationRequest request)
    {
        if (!TryPositive(request.FopdtK, out var k)
            || !TryPositive(request.FopdtTau, out var tau)
            || !TryPositive(request.FopdtTheta, out var theta))
        {
            return PidTuneCalculationResult.Invalid("Enter positive K, Tau and Theta.");
        }

        return request.TuneMethod switch
        {
            PidTuneMethod.FopdtZieglerNicholsPid => PidTuneCalculationResult.Valid(
                1.2 / (k * theta / tau),
                2 * theta,
                0.5 * theta,
                "FOPDT: Ziegler-Nichols PID."),

            PidTuneMethod.FopdtCohenCoonPid => PidTuneCalculationResult.Valid(
                (1 / k) * (tau / theta) * ((4d / 3d) + theta / (4 * tau)),
                theta * (32 + 6 * theta / tau) / (13 + 8 * theta / tau),
                theta * 4 / (11 + 2 * theta / tau),
                "FOPDT: Cohen-Coon PID."),

            PidTuneMethod.FopdtAmigoPid => PidTuneCalculationResult.Valid(
                (1 / k) * (0.2 + 0.45 * tau / theta),
                (0.4 * theta + 0.8 * tau) / (theta + 0.1 * tau) * theta,
                0.5 * theta * tau / (0.3 * theta + tau),
                "FOPDT: AMIGO PID."),

            _ => TryPositive(request.FopdtTauC, out var tauC)
                ? PidTuneCalculationResult.Valid(
                    (1 / k) * (tau / (tauC + theta)),
                    Math.Min(tau, 4 * (tauC + theta)),
                    theta / 2,
                    "FOPDT: SIMC PID, Td = Theta / 2.")
                : PidTuneCalculationResult.Invalid("Enter positive Tau c for SIMC.")
        };
    }

    /// <summary>
    /// Методы для интегрирующего процесса: Ki, Theta и, кроме Ziegler-Nichols, TauC.
    /// </summary>
    private static PidTuneCalculationResult CalculateIntegrating(PidTuneCalculationRequest request)
    {
        if (!TryPositive(request.IntegratingKi, out var ki)
            || !TryPositive(request.IntegratingTheta, out var theta))
        {
            return PidTuneCalculationResult.Invalid("Enter positive ki and Theta.");
        }

        if (request.TuneMethod == PidTuneMethod.IntegratingZieglerNicholsPi)
        {
            return PidTuneCalculationResult.Valid(
                0.9 / (ki * theta),
                3.33 * theta,
                0,
                "Integrating: Ziegler-Nichols PI.");
        }

        if (!TryPositive(request.IntegratingTauC, out var tauC))
            return PidTuneCalculationResult.Invalid("Enter positive Tau c.");

        return request.TuneMethod switch
        {
            PidTuneMethod.IntegratingAveragingPi => PidTuneCalculationResult.Valid(
                0.5 / (ki * (tauC + theta)),
                8 * (tauC + theta),
                0,
                "Integrating: Averaging PI."),

            _ => PidTuneCalculationResult.Valid(
                1 / (ki * (tauC + theta)),
                4 * (tauC + theta),
                0,
                "Integrating: SIMC PI.")
        };
    }

    /// <summary>
    /// Методы закрытого контура по критическому усилению Ku и периоду колебаний Tu.
    /// </summary>
    private static PidTuneCalculationResult CalculateClosedLoop(PidTuneCalculationRequest request)
    {
        if (!TryPositive(request.ClosedLoopKu, out var ku)
            || !TryPositive(request.ClosedLoopTu, out var tu))
        {
            return PidTuneCalculationResult.Invalid("Enter positive Ku and Tu.");
        }

        return request.TuneMethod switch
        {
            PidTuneMethod.ClosedLoopZieglerNicholsSoftPid => PidTuneCalculationResult.Valid(
                0.33 * ku,
                tu / 2,
                tu / 3,
                "Closed loop: Ziegler-Nichols soft PID."),

            PidTuneMethod.ClosedLoopTyreusLuybenPid => PidTuneCalculationResult.Valid(
                ku / 2.2,
                2.2 * tu,
                tu / 6.3,
                "Closed loop: Tyreus-Luyben PID."),

            _ => PidTuneCalculationResult.Valid(
                0.6 * ku,
                tu / 2,
                tu / 8,
                "Closed loop: Ziegler-Nichols PID.")
        };
    }

    private static bool TryPositive(double? source, out double value)
    {
        value = source ?? 0;
        return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
