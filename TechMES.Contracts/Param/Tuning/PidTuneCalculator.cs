namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Общий калькулятор PID/PI-настроек для всех методов, доступных во вкладке Tune.
/// UI передает сюда только выбранную модель, метод и численные параметры.
/// Результаты возвращаются в идеальной/ISA-форме:
/// u = Kp * (e + integral(e) / Ti + Td * de/dt).
/// </summary>
public static class PidTuneCalculator
{
    /// <summary>
    /// Выполняет расчет по выбранной модели процесса и выбранному методу настройки.
    /// </summary>
    public static PidTuneCalculationResult Calculate(PidTuneCalculationRequest request)
    {
        if (request is null)
            return PidTuneCalculationResult.Invalid("Tuning request is required.");

        return request.ProcessModel switch
        {
            PidTuneProcessModel.Fopdt => CalculateFopdt(request),
            PidTuneProcessModel.Integrating => CalculateIntegrating(request),
            PidTuneProcessModel.ClosedLoop => CalculateClosedLoop(request),
            _ => PidTuneCalculationResult.Invalid(
                $"Unsupported process model: {request.ProcessModel}.")
        };
    }

    /// <summary>
    /// Методы для FOPDT-модели: K, Tau, Theta и, для SIMC, желаемая постоянная замкнутого контура TauC.
    /// </summary>
    private static PidTuneCalculationResult CalculateFopdt(PidTuneCalculationRequest request)
    {
        if (!TryNonZero(request.FopdtK, out var k)
            || !TryPositive(request.FopdtTau, out var tau)
            || !TryNonNegative(request.FopdtTheta, out var theta))
        {
            return PidTuneCalculationResult.Invalid(
                "Enter non-zero K, positive Tau and non-negative Theta.");
        }

        return request.TuneMethod switch
        {
            PidTuneMethod.FopdtSimcPi => TryPositive(request.FopdtTauC, out var tauC)
                ? PidTuneCalculationResult.Valid(
                    (1 / k) * (tau / (tauC + theta)),
                    Math.Min(tau, 4 * (tauC + theta)),
                    0,
                    "FOPDT: SIMC PI (ideal/ISA form).")
                : PidTuneCalculationResult.Invalid("Enter positive Tau c for SIMC."),

            PidTuneMethod.FopdtZieglerNicholsPid => RequirePositiveDelay(
                theta,
                () => PidTuneCalculationResult.Valid(
                    1.2 * tau / (k * theta),
                    2 * theta,
                    0.5 * theta,
                    "FOPDT: Ziegler-Nichols PID (ideal/ISA form).")),

            PidTuneMethod.FopdtCohenCoonPid => RequirePositiveDelay(
                theta,
                () => PidTuneCalculationResult.Valid(
                    (1 / k) * (tau / theta) * ((4d / 3d) + theta / (4 * tau)),
                    theta * (32 + 6 * theta / tau) / (13 + 8 * theta / tau),
                    theta * 4 / (11 + 2 * theta / tau),
                    "FOPDT: Cohen-Coon PID (ideal/ISA form).")),

            PidTuneMethod.FopdtAmigoPid => RequirePositiveDelay(
                theta,
                () => PidTuneCalculationResult.Valid(
                    (1 / k) * (0.2 + 0.45 * tau / theta),
                    (0.4 * theta + 0.8 * tau) / (theta + 0.1 * tau) * theta,
                    0.5 * theta * tau / (0.3 * theta + tau),
                    "FOPDT: AMIGO PID (ideal/ISA form).")),

            _ => PidTuneCalculationResult.Invalid(
                $"Unsupported FOPDT tuning method: {request.TuneMethod}.")
        };
    }

    /// <summary>
    /// Методы для интегрирующего процесса: Ki, Theta и, кроме Ziegler-Nichols, TauC.
    ///
    /// Важно: для SIMC/Averaging допускается Theta=0.
    /// Положительная Theta обязательна только для Ziegler-Nichols,
    /// потому что его формула содержит деление на Theta.
    /// </summary>
    private static PidTuneCalculationResult CalculateIntegrating(PidTuneCalculationRequest request)
    {
        if (!TryNonZero(request.IntegratingKi, out var ki)
            || !TryNonNegative(request.IntegratingTheta, out var theta))
        {
            return PidTuneCalculationResult.Invalid(
                "Enter non-zero ki and non-negative Theta.");
        }

        if (request.TuneMethod == PidTuneMethod.IntegratingZieglerNicholsPi)
        {
            if (theta <= 0)
            {
                return PidTuneCalculationResult.Invalid(
                    "Ziegler-Nichols requires positive Theta.");
            }

            return PidTuneCalculationResult.Valid(
                0.9 / (ki * theta),
                3.33 * theta,
                0,
                "Integrating: Ziegler-Nichols PI.");
        }

        if (request.TuneMethod is not PidTuneMethod.IntegratingSimcPi
            and not PidTuneMethod.IntegratingAveragingPi)
        {
            return PidTuneCalculationResult.Invalid(
                $"Unsupported integrating-process tuning method: {request.TuneMethod}.");
        }

        if (!TryPositive(request.IntegratingTauC, out var tauC))
            return PidTuneCalculationResult.Invalid("Enter positive Tau c.");

        return request.TuneMethod switch
        {
            PidTuneMethod.IntegratingAveragingPi => PidTuneCalculationResult.Valid(
                0.5 / (ki * (tauC + theta)),
                8 * (tauC + theta),
                0,
                "Integrating: custom averaging PI."),

            PidTuneMethod.IntegratingSimcPi => PidTuneCalculationResult.Valid(
                1 / (ki * (tauC + theta)),
                4 * (tauC + theta),
                0,
                "Integrating: SIMC PI."),

            _ => PidTuneCalculationResult.Invalid(
                $"Unsupported integrating-process tuning method: {request.TuneMethod}.")
        };
    }

    /// <summary>
    /// Методы закрытого контура по критическому усилению Ku и периоду колебаний Tu.
    /// Вариант с коэффициентом 0.33 Ku является смягченной модификацией
    /// Ziegler-Nichols из исходного Excel.
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
                "Closed loop: modified Ziegler-Nichols (some overshoot) PID."),

            PidTuneMethod.ClosedLoopTyreusLuybenPid => PidTuneCalculationResult.Valid(
                ku / 2.2,
                2.2 * tu,
                tu / 6.3,
                "Closed loop: Tyreus-Luyben PID."),

            PidTuneMethod.ClosedLoopZieglerNicholsPid => PidTuneCalculationResult.Valid(
                0.6 * ku,
                tu / 2,
                tu / 8,
                "Closed loop: Ziegler-Nichols PID."),

            _ => PidTuneCalculationResult.Invalid(
                $"Unsupported closed-loop tuning method: {request.TuneMethod}.")
        };
    }

    private static PidTuneCalculationResult RequirePositiveDelay(
        double theta,
        Func<PidTuneCalculationResult> calculate)
    {
        return theta > 0
            ? calculate()
            : PidTuneCalculationResult.Invalid(
                "The selected tuning method requires positive Theta.");
    }

    private static bool TryPositive(double? source, out double value)
    {
        value = source ?? 0;
        return value > 0 && double.IsFinite(value);
    }

    private static bool TryNonNegative(double? source, out double value)
    {
        value = source ?? -1;
        return value >= 0 && double.IsFinite(value);
    }

    private static bool TryNonZero(double? source, out double value)
    {
        value = source ?? 0;
        return Math.Abs(value) > 1e-12 && double.IsFinite(value);
    }
}
