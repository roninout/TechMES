using System.Globalization;
using TechMES.Contracts.Param.Tuning;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Формирует русскоязычную справку PID Tune.
///
/// Важно:
/// - формулы настройки Kp/Ti/Td повторяют PidTuneCalculator;
/// - алгоритмы идентификации описывают фактическую реализацию TechMES;
/// - численные пороги берутся из PidTuneIdentificationRules, поэтому
///   справка и расчетное ядро используют одни и те же значения.
/// </summary>
internal static class PidTuneInfoCatalog
{
    public static PidTuneInfoContent Create(
        PidTuneCalculationRequest request,
        PidTuneCalculationResult result,
        PidProcessIdentificationResult? identification)
    {
        return new PidTuneInfoContent(
            CreateModelInfo(request),
            CreateIdentificationInfo(request, identification),
            CreateMethodInfo(request.TuneMethod),
            CreateResultValues(result),
            CreateSources(request),
            result.IsValid);
    }

    /// <summary>
    /// Описание физической/математической модели и текущих параметров формы.
    /// </summary>
    private static PidTuneModelInfo CreateModelInfo(
        PidTuneCalculationRequest request)
    {
        return request.ProcessModel switch
        {
            PidTuneProcessModel.Integrating =>
                new PidTuneModelInfo(
                    "Интегрирующий процесс с запаздыванием",
                    "G(s) = ki * exp(-Theta * s) / s",
                    "Модель применяется к объектам без самовыравнивания: после ступени OUT "
                    + "PV не обязана выходить на новое плато, а продолжает изменяться примерно "
                    + "с постоянной скоростью. Поэтому TechMES определяет не конечное DeltaPV, "
                    + "а изменение наклона PV после реакции объекта.",
                    new[]
                    {
                        Value(
                            "ki",
                            "Интегрирующий коэффициент процесса. В TechMES: "
                            + "ki = (изменение наклона PV) / DeltaOUT.",
                            request.IntegratingKi),

                        Value(
                            "Theta",
                            "Оцененное чистое запаздывание между ступенью OUT "
                            + "и началом изменения наклона PV.",
                            request.IntegratingTheta,
                            "с"),

                        Value(
                            "Tau c",
                            "Параметр желаемого быстродействия замкнутого контура. "
                            + "Это параметр настройки, а не физическая постоянная объекта.",
                            request.IntegratingTauC,
                            "с")
                    }),

            PidTuneProcessModel.ClosedLoop =>
                new PidTuneModelInfo(
                    "Критические колебания замкнутого контура",
                    "Ku = current Test Kp;  Tu = median(periods of e(t));  e(t) = PV(t) - SP(t)",
                    "ClosedLoop не строит передаточную функцию объекта. Во время ultimate-gain "
                    + "испытания интегральную и дифференциальную части регулятора отключают, "
                    + "а пропорциональное усиление доводят до устойчивых незатухающих колебаний. "
                    + "TechMES проверяет колебания ошибки PV-SP, а не одного PV, чтобы изменение "
                    + "самого SP не было ошибочно принято за критические колебания.",
                    new[]
                    {
                        Value(
                            "Ku",
                            "Критическое усиление. В TechMES принимается равным текущему "
                            + "online-значению Test Kp только после подтверждения устойчивых "
                            + "незатухающих колебаний.",
                            request.ClosedLoopKu),

                        Value(
                            "Tu",
                            "Критический период. Определяется как медиана периодов "
                            + "последовательных восходящих zero-crossing ошибки PV-SP.",
                            request.ClosedLoopTu,
                            "с")
                    }),

            _ =>
                new PidTuneModelInfo(
                    "FOPDT: объект первого порядка с запаздыванием",
                    "G(s) = K * exp(-Theta * s) / (Tau * s + 1)",
                    "FOPDT описывает устойчивый самовыравнивающийся объект. Текущий TechMES "
                    + "НЕ вычисляет Tau/Theta по отдельным точкам 28,3% и 63,2%. После найденной "
                    + "ступени OUT одновременно подбираются K, Tau и Theta по всей видимой "
                    + "кривой PV методом наименьших квадратов.",
                    new[]
                    {
                        Value(
                            "K",
                            "Статический коэффициент усиления процесса. После whole-curve fit: "
                            + "K = A / DeltaOUT, где A — fitted полная амплитуда PV.",
                            request.FopdtK),

                        Value(
                            "Tau",
                            "Постоянная времени FOPDT. После окончания Theta за одну Tau "
                            + "идеальная модель проходит 1-exp(-1) = 63,2% полного отклика.",
                            request.FopdtTau,
                            "с"),

                        Value(
                            "Theta",
                            "Оцененное чистое транспортное/эффективное запаздывание.",
                            request.FopdtTheta,
                            "с"),

                        Value(
                            "Tau c",
                            "Желаемая постоянная времени замкнутого контура для SIMC. "
                            + "Автоматическое начальное значение TechMES: max(Theta, dt).",
                            request.FopdtTauC,
                            "с")
                    })
        };
    }

    /// <summary>
    /// Подробно описывает именно тот алгоритм идентификации,
    /// который выполняет PidProcessIdentifier.
    /// </summary>
    private static PidTuneIdentificationInfo CreateIdentificationInfo(
        PidTuneCalculationRequest request,
        PidProcessIdentificationResult? identification)
    {
        return request.ProcessModel switch
        {
            PidTuneProcessModel.Integrating =>
                CreateIntegratingIdentification(identification),

            PidTuneProcessModel.ClosedLoop =>
                CreateClosedLoopIdentification(identification),

            _ =>
                CreateFopdtIdentification(identification)
        };
    }

    private static PidTuneIdentificationInfo CreateFopdtIdentification(
        PidProcessIdentificationResult? identification)
    {
        return new PidTuneIdentificationInfo(
            "Как TechMES определяет K, Tau и Theta",
            "После синхронизации PV и OUT алгоритм ищет один быстрый и удерживаемый "
            + "скачок OUT. Исходный PV0 берется как медиана нескольких точек перед "
            + "ступенью. Затем Theta и Tau перебираются, а для каждой пары оптимальная "
            + "полная амплитуда A вычисляется аналитически по МНК. Побеждает модель "
            + "с минимальной суммой квадратов ошибок по всей post-step кривой.",
            new[]
            {
                "DeltaOUT = OUT_after - OUT_before",
                "f_i(Theta,Tau) = 0, если t_i <= Theta",
                "f_i(Theta,Tau) = 1 - exp(-(t_i - Theta) / Tau), если t_i > Theta",
                "A(Theta,Tau) = Sum[f_i * (PV_i - PV0)] / Sum[f_i^2]",
                "PVhat_i = PV0 + A * f_i",
                "SSE = Sum[(PV_i - PVhat_i)^2]",
                "K = A / DeltaOUT",
                "RMSE = sqrt(SSE / N)",
                "R^2 = 1 - SSE / Sum[(PV_i - mean(PV))^2]",
                "ObservedFraction = 1 - exp(-(Tobs - Theta) / Tau)",
                "TauC(auto) = max(Theta, dt)"
            },
            new[]
            {
                "PV и OUT нормализуются по времени; NaN/Infinity удаляются, одинаковые timestamps усредняются.",
                "PV и OUT сопоставляются по ближайшему времени. Слишком удаленные пары отбрасываются; для FOPDT требуется минимум 12 синхронизированных пар.",
                "Ступень OUT должна оставлять минимум 4 пары до скачка и минимум 8 пар после него.",
                "Ступень OUT ищется по сочетанию мгновенного скачка и разницы медианных уровней до/после.",
                $"Мгновенный скачок должен быть >= {Percent(PidTuneIdentificationRules.MinimumStepInstantFraction)} устойчивой DeltaOUT.",
                $"Медиана хвоста OUT должна отличаться от нового уровня не более чем на {Percent(PidTuneIdentificationRules.MaximumStepTailLevelErrorRatio)} |DeltaOUT|.",
                $"Хвост OUT должен иметь (P95-P05)/|DeltaOUT| <= {Percent(PidTuneIdentificationRules.MaximumOutputTailRangeRatio)}.",
                "PV0 = медиана до 8 исходных точек перед ступенью; шум исходного PV оценивается стандартным отклонением этих точек.",
                "Post-step окно должно быть длиннее max(4*dt, 1 c). Для перебора оно равномерно уменьшается максимум до 1200 точек, но quality metrics затем считаются по полному post-step набору.",
                "Грубый поиск Theta: 0 ... min(0,60*Tobs, Tobs-dt), 40 интервалов.",
                "Грубый поиск Tau: логарифмическая сетка от max(0,5*dt; 0,001 c) до max(10*TauMin; 4*Tobs), 48 интервалов.",
                "После грубого поиска выполняется локальный уточняющий перебор 30 x 30 около лучшей пары Theta/Tau.",
                $"Fitted амплитуда A должна быть не меньше {PidTuneIdentificationRules.MinimumFopdtSignalToNoiseSigma:0.#} sigma исходного PV-шума.",
                $"Принимается только fit с R^2 >= {PidTuneIdentificationRules.MinimumFopdtR2:0.##}.",
                $"Выбранное окно должно реально показать минимум {Percent(PidTuneIdentificationRules.MinimumFopdtObservedResponseFraction)} fitted-отклика, то есть примерно одну Tau после Theta.",
                "Условие 63,2% используется только как проверка наблюдаемости найденной модели; Tau не вычисляется по одной точке 63,2%."
            },
            CreateFopdtDiagnostics(identification),
            identification?.IsSuccess == true
                ? null
                : "Если видна только ранняя часть экспоненты, высокий R^2 сам по себе недостаточен: "
                  + "K и Tau могут быть взаимозаменяемыми. Поэтому TechMES отдельно требует "
                  + "наблюдать минимум одну Tau fitted-модели.");
    }

    private static PidTuneIdentificationInfo CreateIntegratingIdentification(
        PidProcessIdentificationResult? identification)
    {
        return new PidTuneIdentificationInfo(
            "Как TechMES определяет ki и Theta",
            "Интегрирующий объект не имеет обязательного конечного плато PV. Поэтому TechMES "
            + "аппроксимирует весь выбранный участок кусочно-линейной моделью: до реакции "
            + "разрешен исходный дрейф b0, после Theta наклон изменяется на c. Для каждого "
            + "кандидата Theta коэффициенты a, b0 и c находятся линейным МНК.",
            new[]
            {
                "PVhat(t) = a + b0*t + c*max(0, t-Theta)",
                "[a, b0, c] = arg min Sum[(PV_i - PVhat_i)^2]",
                "Slope_before = b0",
                "Slope_after = b0 + c",
                "DeltaSlope = c",
                "ki = c / DeltaOUT",
                "RMSE = sqrt(SSE / N)",
                "R^2 = 1 - SSE / Sum[(PV_i - mean(PV))^2]",
                "TauC(auto) = max(Theta, dt)"
            },
            new[]
            {
                "PV и OUT синхронизируются по времени тем же способом, что и для FOPDT; требуется минимум 12 общих пар.",
                "Ступень должна оставлять минимум 4 пары до скачка и минимум 8 пар после него.",
                "Должна присутствовать одна быстрая и удерживаемая ступень OUT.",
                $"Мгновенный скачок должен быть >= {Percent(PidTuneIdentificationRules.MinimumStepInstantFraction)} устойчивой DeltaOUT.",
                $"Медиана хвоста OUT должна отличаться от нового уровня не более чем на {Percent(PidTuneIdentificationRules.MaximumStepTailLevelErrorRatio)} |DeltaOUT|.",
                $"Робастный разброс хвоста OUT должен иметь (P95-P05)/|DeltaOUT| <= {Percent(PidTuneIdentificationRules.MaximumOutputTailRangeRatio)}.",
                "После ступени должно быть больше max(5*dt, 1 c) наблюдения. Для перебора МНК набор равномерно уменьшается максимум до 1600 точек.",
                "Для каждого Theta решается нормальная система МНК по базисам [1, t, max(0,t-Theta)].",
                "Грубый перебор Theta: 0 ... min(0,40*Tpost, Tpost-2*dt), 100 интервалов.",
                "После него выполняется локальное уточнение Theta в 60 интервалах.",
                $"Эффект изменения наклона |c|*Tpost должен превышать {PidTuneIdentificationRules.MinimumIntegratingSlopeSignalToNoiseSigma:0.#} sigma исходного PV-шума.",
                $"Принимается только кусочно-линейный fit с R^2 >= {PidTuneIdentificationRules.MinimumIntegratingR2:0.##}.",
                "Знак ki сохраняется: он зависит от направления реакции PV на изменение OUT.",
                "Theta может быть равна 0. Для Ziegler-Nichols PI затем требуется Theta > 0, потому что формула содержит деление на Theta."
            },
            CreateIntegratingDiagnostics(identification),
            null);
    }

    private static PidTuneIdentificationInfo CreateClosedLoopIdentification(
        PidProcessIdentificationResult? identification)
    {
        return new PidTuneIdentificationInfo(
            "Как TechMES определяет Ku и Tu",
            "ClosedLoop-анализ выполняется по ошибке e(t)=PV(t)-SP(t). Это принципиально: "
            + "если сам SP периодически изменяется, колебания PV не должны быть приняты за "
            + "ultimate oscillation. Из ошибки удаляется линейный дрейф, затем применяется "
            + "слабое трехточечное сглаживание и устойчивый zero-crossing detector.",
            new[]
            {
                "e_i = PV_i - SP_i",
                "e_fit(t) = a + b*t",
                "r_i = e_i - e_fit(t_i)",
                "Arobust = (P95(r_smooth) - P05(r_smooth)) / 2",
                $"hysteresis = {PidTuneIdentificationRules.ClosedLoopCrossingHysteresisFraction:0.##} * Arobust",
                "period_i = crossing_(i+1) - crossing_i",
                "Tu = median(accepted periods)",
                "PeriodCV = std(periods) / mean(periods)",
                "Acycle_i = (max(cycle_i) - min(cycle_i)) / 2",
                "AmplitudeCV = std(Acycle) / mean(Acycle)",
                "AmplitudeTrend = mean(last cycles) / mean(first cycles)",
                "SPvariation = (P95(SP) - P05(SP)) / peakToPeak(detrended error)",
                "SPdrift = |slope(SP)| * Tobs / peakToPeak(detrended error)",
                "Ku = current online Test Kp"
            },
            new[]
            {
                "PV и SP должны иметь минимум 20 общих точек в выбранном временном окне; после синхронизации набор при необходимости равномерно уменьшается максимум до 4000 точек.",
                $"SPvariation <= {Percent(PidTuneIdentificationRules.MaximumClosedLoopSetpointVariationRatio)} и SPdrift <= {Percent(PidTuneIdentificationRules.MaximumClosedLoopSetpointDriftRatio)}; иначе колебание может быть вынуждено самим SP.",
                "Из e(t)=PV-SP удаляется линейный тренд a+b*t, чтобы медленный дрейф не смещал zero crossing.",
                "Остаток сглаживается moving average радиуса 1, то есть обычно по трем соседним точкам.",
                $"Crossing detector использует гистерезис {Percent(PidTuneIdentificationRules.ClosedLoopCrossingHysteresisFraction)} робастной амплитуды и считает только восходящие переходы.",
                $"Нужно минимум {PidTuneIdentificationRules.MinimumClosedLoopCycles} полных цикла.",
                $"Единичные периоды вне {PidTuneIdentificationRules.ClosedLoopPeriodFilterLowerRatio:0.##}...{PidTuneIdentificationRules.ClosedLoopPeriodFilterUpperRatio:0.##} начальной медианы считаются ложными/пропущенными crossing.",
                $"Итоговый период должен быть >= {PidTuneIdentificationRules.MinimumClosedLoopPeriodDtMultiple:0.#}*dt, иначе колебание слишком похоже на дискретный шум.",
                $"PeriodCV должен быть <= {Percent(PidTuneIdentificationRules.MaximumClosedLoopPeriodCv)}.",
                $"AmplitudeCV должен быть <= {Percent(PidTuneIdentificationRules.MaximumClosedLoopAmplitudeCv)}.",
                $"AmplitudeTrend сравнивает среднее максимум двух первых принятых циклов со средним максимум двух последних. Значение < {PidTuneIdentificationRules.MinimumClosedLoopAmplitudeTrendRatio:0.##} означает затухающие колебания; > {PidTuneIdentificationRules.MaximumClosedLoopAmplitudeTrendRatio:0.##} — растущие.",
                "Только после прохождения этих критериев текущий online Test Kp принимается как Ku."
            },
            CreateClosedLoopDiagnostics(identification),
            "Test Kp не архивируется. При расчете по историческому PV/SP значение Ku берется "
            + "из ТЕКУЩЕГО online Test Kp. Перед использованием результата обязательно убедитесь, "
            + "что именно это Kp было активно в выбранном историческом интервале.");
    }

    private static IReadOnlyList<PidTuneValueInfo> CreateFopdtDiagnostics(
        PidProcessIdentificationResult? identification)
    {
        return new[]
        {
            Value("DeltaOUT", "Найденная величина ступени OUT.", identification?.DeltaOut),
            Value("PV0", "Медианный исходный уровень PV перед ступенью.", identification?.PvBaseline),
            Value("A", "Полная fitted-амплитуда PV = K*DeltaOUT.", identification?.ResponseAmplitude),
            Value("R²", "Коэффициент детерминации whole-curve fit.", identification?.R2),
            Value("RMSE", "Среднеквадратичная ошибка fit в единицах PV.", identification?.Rmse),
            PercentValue("Observed", "Реально наблюдаемая доля полного fitted FOPDT-отклика.", identification?.ObservedResponseFraction),
            PercentValue("OUT tail", "Робастный разброс хвоста OUT относительно |DeltaOUT|.", identification?.OutputTailRangeRatio),
            Value("dt", "Оцененный медианный шаг тренда.", identification is null ? null : identification.DtSeconds, "с"),
            IntegerValue("N", "Количество синхронизированных точек, использованных идентификатором.", identification?.PointsUsed)
        };
    }

    private static IReadOnlyList<PidTuneValueInfo> CreateIntegratingDiagnostics(
        PidProcessIdentificationResult? identification)
    {
        return new[]
        {
            Value("DeltaOUT", "Найденная величина ступени OUT.", identification?.DeltaOut),
            Value("b0", "Оцененный исходный наклон PV до реакции.", identification?.BaseSlope),
            Value("c", "Изменение наклона PV после Theta.", identification?.SlopeChange),
            Value("R²", "Коэффициент детерминации кусочно-линейной модели.", identification?.R2),
            Value("RMSE", "Среднеквадратичная ошибка модели в единицах PV.", identification?.Rmse),
            PercentValue("OUT tail", "Робастный разброс хвоста OUT относительно |DeltaOUT|.", identification?.OutputTailRangeRatio),
            Value("dt", "Оцененный медианный шаг тренда.", identification is null ? null : identification.DtSeconds, "с"),
            IntegerValue("N", "Количество синхронизированных точек.", identification?.PointsUsed)
        };
    }

    private static IReadOnlyList<PidTuneValueInfo> CreateClosedLoopDiagnostics(
        PidProcessIdentificationResult? identification)
    {
        return new[]
        {
            Value("A", "Средняя амплитуда принятых циклов detrended ошибки PV-SP.", identification?.OscillationAmplitude),
            PercentValue("Period CV", "Коэффициент вариации принятых периодов.", identification?.PeriodCv),
            PercentValue("Ampl. CV", "Коэффициент вариации амплитуд циклов.", identification?.AmplitudeCv),
            Value("A late/early", "Отношение средней поздней амплитуды к ранней.", identification?.AmplitudeTrendRatio),
            PercentValue("SP variation", "P95-P05 SP относительно peak-to-peak ошибки.", identification?.SetpointVariationRatio),
            PercentValue("SP drift", "Линейный дрейф SP относительно peak-to-peak ошибки.", identification?.SetpointDriftRatio),
            IntegerValue("Cycles", "Количество полных циклов, использованных для проверки амплитуды.", identification?.CyclesUsed),
            Value("dt", "Оцененный медианный шаг тренда.", identification is null ? null : identification.DtSeconds, "с"),
            IntegerValue("N", "Количество синхронизированных PV/SP-точек.", identification?.PointsUsed)
        };
    }

    /// <summary>
    /// Точные формулы настройки. Они должны оставаться идентичными PidTuneCalculator.
    /// </summary>
    private static PidTuneMethodInfo CreateMethodInfo(
        string method)
    {
        return method switch
        {
            PidTuneMethod.FopdtZieglerNicholsPid =>
                Method(
                    "Ziegler-Nichols PID для FOPDT",
                    "Классическое open-loop/process-reaction правило. Обычно дает агрессивные "
                    + "стартовые настройки. В TechMES коэффициенты K/Tau/Theta получаются "
                    + "современным whole-curve fit, но затем подставляются в классические формулы.",
                    "Kp = 1.2 * Tau / (K * Theta)",
                    "Ti = 2 * Theta",
                    "Td = 0.5 * Theta"),

            PidTuneMethod.FopdtCohenCoonPid =>
                Method(
                    "Cohen-Coon PID для FOPDT",
                    "Open-loop правило, учитывающее отношение запаздывания Theta к постоянной "
                    + "времени Tau. Требует положительного Theta.",
                    "Kp = (Tau / (K * Theta)) * (4/3 + Theta / (4 * Tau))",
                    "Ti = Theta * (32 + 6 * Theta / Tau) / (13 + 8 * Theta / Tau)",
                    "Td = 4 * Theta / (11 + 2 * Theta / Tau)"),

            PidTuneMethod.FopdtAmigoPid =>
                Method(
                    "AMIGO PID для FOPDT",
                    "Правило Åström-Hägglund, полученное как приближение MIGO/robust-design "
                    + "для K-L-T модели. По сравнению с классическим Ziegler-Nichols "
                    + "ориентировано на лучший компромисс быстродействия и робастности.",
                    "Kp = (1 / K) * (0.2 + 0.45 * Tau / Theta)",
                    "Ti = Theta * (0.4 * Theta + 0.8 * Tau) / (Theta + 0.1 * Tau)",
                    "Td = 0.5 * Theta * Tau / (0.3 * Theta + Tau)"),

            PidTuneMethod.IntegratingZieglerNicholsPi =>
                Method(
                    "Ziegler-Nichols PI для интегрирующего процесса",
                    "PI-правило по интегрирующему коэффициенту ki и запаздыванию Theta. "
                    + "Theta должна быть строго положительной.",
                    "Kp = 0.9 / (ki * Theta)",
                    "Ti = 3.33 * Theta",
                    "Td = 0"),

            PidTuneMethod.IntegratingAveragingPi =>
                Method(
                    "Averaging PI для интегрирующего процесса",
                    "Внутреннее правило исходного TechMES/Excel. Оно намеренно консервативнее "
                    + "SIMC и не выдается за отдельный опубликованный стандарт.",
                    "Kp = 0.5 / (ki * (Tau c + Theta))",
                    "Ti = 8 * (Tau c + Theta)",
                    "Td = 0"),

            PidTuneMethod.IntegratingSimcPi =>
                Method(
                    "SIMC PI для интегрирующего процесса",
                    "Модельное правило Skogestad для интегрирующего объекта. Tau c задает "
                    + "желаемую скорость замкнутого контура.",
                    "Kp = 1 / (ki * (Tau c + Theta))",
                    "Ti = 4 * (Tau c + Theta)",
                    "Td = 0"),

            PidTuneMethod.ClosedLoopZieglerNicholsSoftPid =>
                Method(
                    "Modified Ziegler-Nichols: some overshoot",
                    "Смягченный closed-loop вариант ZN. В справочной литературе встречается "
                    + "как modified ZN II / some overshoot.",
                    "Kp = 0.33 * Ku",
                    "Ti = Tu / 2",
                    "Td = Tu / 3"),

            PidTuneMethod.ClosedLoopTyreusLuybenPid =>
                Method(
                    "Tyreus-Luyben PID",
                    "Closed-loop правило по ultimate gain/period. Обычно менее агрессивно, "
                    + "чем классический Ziegler-Nichols.",
                    "Kp = Ku / 2.2",
                    "Ti = 2.2 * Tu",
                    "Td = Tu / 6.3"),

            PidTuneMethod.ClosedLoopZieglerNicholsPid =>
                Method(
                    "Ziegler-Nichols PID по критическим колебаниям",
                    "Классическое closed-loop ultimate-gain правило. Требует действительно "
                    + "устойчивых незатухающих колебаний при Ku.",
                    "Kp = 0.6 * Ku",
                    "Ti = Tu / 2",
                    "Td = Tu / 8"),

            _ =>
                Method(
                    "SIMC PI для FOPDT",
                    "Простое модельное правило Skogestad. Tau c — основной параметр "
                    + "компромисса: увеличение Tau c делает контур медленнее и робастнее.",
                    "Kp = (1 / K) * Tau / (Tau c + Theta)",
                    "Ti = min(Tau, 4 * (Tau c + Theta))",
                    "Td = 0")
        };
    }

    private static IReadOnlyList<PidTuneValueInfo> CreateResultValues(
        PidTuneCalculationResult result)
    {
        return new[]
        {
            Value(
                "Kp",
                "Пропорциональный коэффициент идеальной/ISA формы. "
                + "Масштабирует текущую ошибку e=SP-PV.",
                result.Kp),

            Value(
                "Ti",
                "Время интегрирования. Интегральный вклад равен (1/Ti)*integral(e)dt.",
                result.Ti,
                "с"),

            Value(
                "Td",
                "Время дифференцирования. Вклад производной равен Td*de/dt.",
                result.Td,
                "с")
        };
    }

    /// <summary>
    /// Ссылки на первоисточники/публикации.
    ///
    /// Сам robust-fit TechMES (grid search, R² thresholds, CV thresholds)
    /// является реализационным решением проекта, поэтому честно помечается
    /// отдельно и не приписывается публикациям.
    /// </summary>
    private static IReadOnlyList<PidTuneSourceInfo> CreateSources(
        PidTuneCalculationRequest request)
    {
        var result = new List<PidTuneSourceInfo>();

        void Add(
            string title,
            string? url,
            string scope)
        {
            if (result.Any(item =>
                    string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            result.Add(new PidTuneSourceInfo(title, url, scope));
        }

        Add(
            "TechMES PID Tune identification implementation",
            null,
            "Whole-curve FOPDT fit, кусочно-линейный Integrating fit, "
            + "PV-SP oscillation detector и численные acceptance thresholds.");

        if (request.ProcessModel is PidTuneProcessModel.Fopdt
            or PidTuneProcessModel.Integrating)
        {
            Add(
                "S. Skogestad, Simple analytic rules for model reduction and PID controller tuning, Journal of Process Control 13 (2003) 291-309",
                "https://doi.org/10.1016/S0959-1524(02)00062-8",
                "FOPDT/integrating process models и SIMC tuning.");
        }

        if (request.ProcessModel == PidTuneProcessModel.ClosedLoop)
        {
            Add(
                "J. G. Ziegler, N. B. Nichols, Optimum Settings for Automatic Controllers (1942)",
                "https://doi.org/10.1115/1.4019264",
                "Ultimate-gain experiment, Ku/Tu и классические Ziegler-Nichols rules.");
        }

        switch (request.TuneMethod)
        {
            case PidTuneMethod.FopdtZieglerNicholsPid:
            case PidTuneMethod.IntegratingZieglerNicholsPi:
            case PidTuneMethod.ClosedLoopZieglerNicholsPid:
                Add(
                    "J. G. Ziegler, N. B. Nichols, Optimum Settings for Automatic Controllers (1942)",
                    "https://doi.org/10.1115/1.4019264",
                    "Классические Ziegler-Nichols tuning rules.");
                break;

            case PidTuneMethod.FopdtCohenCoonPid:
                Add(
                    "G. H. Cohen, G. A. Coon, Theoretical Consideration of Retarded Control, Trans. ASME 75 (1953) 827-834",
                    "https://cir.nii.ac.jp/crid/1572543025662654976?lang=en",
                    "Оригинальная работа Cohen-Coon.");
                break;

            case PidTuneMethod.FopdtAmigoPid:
                Add(
                    "K. J. Åström, T. Hägglund, Revisiting the Ziegler-Nichols step response method for PID control, Journal of Process Control 14 (2004)",
                    "https://doi.org/10.1016/j.jprocont.2004.01.002",
                    "AMIGO/MIGO-derived FOPDT PID rule.");
                break;

            case PidTuneMethod.FopdtSimcPi:
            case PidTuneMethod.IntegratingSimcPi:
                Add(
                    "S. Skogestad, Simple analytic rules for model reduction and PID controller tuning, Journal of Process Control 13 (2003) 291-309",
                    "https://doi.org/10.1016/S0959-1524(02)00062-8",
                    "SIMC PI formulas.");
                break;

            case PidTuneMethod.IntegratingAveragingPi:
                Add(
                    "Внутреннее правило исходного TechMES PID-калькулятора",
                    null,
                    "Averaging PI; опубликованный первоисточник для этой конкретной формулы не заявляется.");
                break;

            case PidTuneMethod.ClosedLoopZieglerNicholsSoftPid:
                Add(
                    "Zhang et al., Data-driven direct automatic tuning scheme for fixed-structure digital controllers of hybrid systems, IET Control Theory & Applications (2019)",
                    "https://doi.org/10.1049/iet-cta.2018.5165",
                    "Таблица alternative ZN rules: modified ZN II / some overshoot, "
                    + "Kp=0.33Ku, Ti=Pu/2, Td=Pu/3.");
                break;

            case PidTuneMethod.ClosedLoopTyreusLuybenPid:
                Add(
                    "W. L. Luyben, Tuning Proportional-Integral-Derivative Controllers for Integrator/Deadtime Processes, Ind. Eng. Chem. Res. 35 (1996) 3480-3483",
                    "https://doi.org/10.1021/ie9600699",
                    "Опубликованное развитие Tyreus-Luyben PID tuning.");

                Add(
                    "Michigan Technological University, Ziegler-Nichols / Tyreus-Luyben tuning chart",
                    "https://pages.mtu.edu/~tbco/cm416/zn.html",
                    "Сводная closed-loop таблица Ku/Pu, включая Ku/2.2, 2.2Pu и Pu/6.3.");
                break;
        }

        return result;
    }

    private static PidTuneMethodInfo Method(
        string title,
        string description,
        string kp,
        string ti,
        string td)
    {
        return new PidTuneMethodInfo(
            title,
            description,
            new[] { kp, ti, td });
    }

    private static PidTuneValueInfo Value(
        string symbol,
        string description,
        double? value,
        string? unit = null)
    {
        var formatted = value.HasValue
            ? value.Value.ToString("0.######", CultureInfo.InvariantCulture)
            : "не рассчитано";

        if (value.HasValue
            && !string.IsNullOrWhiteSpace(unit))
        {
            formatted += " " + unit;
        }

        return new PidTuneValueInfo(
            symbol,
            description,
            formatted);
    }

    private static PidTuneValueInfo PercentValue(
        string symbol,
        string description,
        double? value)
    {
        var formatted = value.HasValue
            ? (value.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %"
            : "не рассчитано";

        return new PidTuneValueInfo(
            symbol,
            description,
            formatted);
    }

    private static PidTuneValueInfo IntegerValue(
        string symbol,
        string description,
        int? value)
    {
        return new PidTuneValueInfo(
            symbol,
            description,
            value?.ToString(CultureInfo.InvariantCulture)
            ?? "не рассчитано");
    }

    private static string Percent(
        double value)
    {
        return (value * 100)
            .ToString("0.##", CultureInfo.InvariantCulture)
            + " %";
    }
}

/// <summary>
/// Полное содержимое контекстной справки PID Tune.
/// </summary>
internal sealed record PidTuneInfoContent(
    PidTuneModelInfo Model,
    PidTuneIdentificationInfo Identification,
    PidTuneMethodInfo Method,
    IReadOnlyList<PidTuneValueInfo> ResultValues,
    IReadOnlyList<PidTuneSourceInfo> Sources,
    bool HasValidResult);

internal sealed record PidTuneModelInfo(
    string Title,
    string Equation,
    string Description,
    IReadOnlyList<PidTuneValueInfo> Values);

internal sealed record PidTuneIdentificationInfo(
    string Title,
    string Description,
    IReadOnlyList<string> Formulas,
    IReadOnlyList<string> Steps,
    IReadOnlyList<PidTuneValueInfo> Diagnostics,
    string? Warning);

internal sealed record PidTuneMethodInfo(
    string Title,
    string Description,
    IReadOnlyList<string> Formulas);

internal sealed record PidTuneValueInfo(
    string Symbol,
    string Description,
    string CurrentValue);

internal sealed record PidTuneSourceInfo(
    string Title,
    string? Url,
    string Scope);
