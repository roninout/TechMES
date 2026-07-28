using System.Globalization;
using TechMES.Contracts.Param.Tuning;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Формирует русскоязычную справку по моделям процесса и формулам PID Tune.
/// Формулы в этом каталоге намеренно повторяют реализацию <see cref="PidTuneCalculator"/>,
/// чтобы диалог всегда объяснял именно тот расчет, который выполняет приложение.
/// </summary>
internal static class PidTuneInfoCatalog
{
    /// <summary>
    /// Собирает описание выбранной модели, метода, входных параметров и результата.
    /// </summary>
    public static PidTuneInfoContent Create(
        PidTuneCalculationRequest request,
        PidTuneCalculationResult result)
    {
        var model = CreateModelInfo(request);
        var method = CreateMethodInfo(request.TuneMethod);

        return new PidTuneInfoContent(
            model,
            method,
            CreateResultValues(result),
            result.IsValid);
    }

    /// <summary>
    /// Возвращает математическую модель процесса и значения ее коэффициентов.
    /// </summary>
    private static PidTuneModelInfo CreateModelInfo(PidTuneCalculationRequest request)
    {
        return request.ProcessModel switch
        {
            PidTuneProcessModel.Integrating => new PidTuneModelInfo(
                "Интегрирующий процесс с запаздыванием",
                "G(s) = ki * exp(-Theta * s) / s",
                "Модель применяется, когда после ступени OUT значение PV продолжает изменяться "
                + "с приблизительно постоянной скоростью и не выходит на новое установившееся значение.",
                new[]
                {
                    Value("ki", "Скорость изменения PV на единицу изменения OUT.", request.IntegratingKi),
                    Value("Theta", "Чистое транспортное запаздывание процесса.", request.IntegratingTheta, "с"),
                    Value("Tau c", "Желаемая постоянная времени замкнутого контура.", request.IntegratingTauC, "с")
                }),

            PidTuneProcessModel.ClosedLoop => new PidTuneModelInfo(
                "Критические колебания замкнутого контура",
                "Экспериментальные параметры: Ku, Tu",
                "Модель не аппроксимирует передаточную функцию. Для опыта интегральная и "
                + "дифференциальная части отключаются, а Kp повышается до устойчивых незатухающих "
                + "колебаний. Полученные Ku и Tu используются в табличных правилах настройки.",
                new[]
                {
                    Value("Ku", "Критическое усиление, при котором возникают незатухающие колебания.", request.ClosedLoopKu),
                    Value("Tu", "Период установившихся критических колебаний.", request.ClosedLoopTu, "с")
                }),

            _ => new PidTuneModelInfo(
                "FOPDT: объект первого порядка с запаздыванием",
                "G(s) = K * exp(-Theta * s) / (Tau * s + 1)",
                "Модель описывает устойчивый самовыравнивающийся процесс. Автоматическая "
                + "идентификация по видимой области графика находит ступень OUT, вычисляет "
                + "K = DeltaPV / DeltaOUT и оценивает Tau и Theta по точкам 28,3 % и 63,2 % отклика PV.",
                new[]
                {
                    Value("K", "Статический коэффициент усиления процесса: DeltaPV / DeltaOUT.", request.FopdtK),
                    Value("Tau", "Постоянная времени объекта после окончания запаздывания.", request.FopdtTau, "с"),
                    Value("Theta", "Чистое транспортное запаздывание процесса.", request.FopdtTheta, "с"),
                    Value("Tau c", "Желаемая постоянная времени замкнутого контура; используется SIMC.", request.FopdtTauC, "с")
                })
        };
    }

    /// <summary>
    /// Возвращает точные формулы выбранного правила настройки.
    /// </summary>
    private static PidTuneMethodInfo CreateMethodInfo(string method)
    {
        return method switch
        {
            PidTuneMethod.FopdtZieglerNicholsPid => Method(
                "Ziegler-Nichols PID для FOPDT",
                "Классическое разомкнутое правило по кривой реакции. Обычно дает достаточно "
                + "агрессивные стартовые настройки и требует проверки перерегулирования.",
                "Kp = 1.2 * Tau / (K * Theta)",
                "Ti = 2 * Theta",
                "Td = 0.5 * Theta",
                "J. G. Ziegler, N. B. Nichols, Optimum Settings for Automatic Controllers (1942)",
                "https://doi.org/10.1115/1.4019264"),

            PidTuneMethod.FopdtCohenCoonPid => Method(
                "Cohen-Coon PID для FOPDT",
                "Разомкнутое правило, учитывающее отношение запаздывания Theta к постоянной "
                + "времени Tau. Применяется только при положительном Theta.",
                "Kp = (Tau / (K * Theta)) * (4/3 + Theta / (4 * Tau))",
                "Ti = Theta * (32 + 6 * Theta / Tau) / (13 + 8 * Theta / Tau)",
                "Td = 4 * Theta / (11 + 2 * Theta / Tau)",
                "G. H. Cohen, G. A. Coon, Theoretical Consideration of Retarded Control (1953)",
                "https://cir.nii.ac.jp/crid/1571417125550845696"),

            PidTuneMethod.FopdtAmigoPid => Method(
                "AMIGO PID для FOPDT",
                "Робастное правило Astrom-Hagglund. По сравнению с классическим Ziegler-Nichols "
                + "ориентировано на более сбалансированное соотношение быстродействия и устойчивости.",
                "Kp = (1 / K) * (0.2 + 0.45 * Tau / Theta)",
                "Ti = Theta * (0.4 * Theta + 0.8 * Tau) / (Theta + 0.1 * Tau)",
                "Td = 0.5 * Theta * Tau / (0.3 * Theta + Tau)",
                "K. J. Astrom, T. Hagglund, Revisiting the Ziegler-Nichols Step Response Method (2004)",
                "https://doi.org/10.1016/j.jprocont.2004.01.002"),

            PidTuneMethod.IntegratingZieglerNicholsPi => Method(
                "Ziegler-Nichols PI для интегрирующего процесса",
                "PI-настройка по коэффициенту интегрирования ki и запаздыванию Theta.",
                "Kp = 0.9 / (ki * Theta)",
                "Ti = 3.33 * Theta",
                "Td = 0",
                "J. G. Ziegler, N. B. Nichols, Optimum Settings for Automatic Controllers (1942)",
                "https://doi.org/10.1115/1.4019264"),

            PidTuneMethod.IntegratingAveragingPi => Method(
                "Averaging PI для интегрирующего процесса",
                "Пользовательское усредняющее правило из исходного Excel-файла TechMES. "
                + "Оно сделано консервативнее SIMC и не является отдельным опубликованным стандартом.",
                "Kp = 0.5 / (ki * (Tau c + Theta))",
                "Ti = 8 * (Tau c + Theta)",
                "Td = 0",
                "Внутреннее правило исходного PID-калькулятора TechMES",
                null),

            PidTuneMethod.IntegratingSimcPi => Method(
                "SIMC PI для интегрирующего процесса",
                "Настройка Skogestad для интегрирующего объекта с явным параметром быстродействия Tau c.",
                "Kp = 1 / (ki * (Tau c + Theta))",
                "Ti = 4 * (Tau c + Theta)",
                "Td = 0",
                "S. Skogestad, Simple Analytic Rules for Model Reduction and PID Controller Tuning",
                "https://doi.org/10.1016/S0959-1524(02)00062-8"),

            PidTuneMethod.ClosedLoopZieglerNicholsSoftPid => Method(
                "Модифицированный Ziegler-Nichols PID",
                "Смягченный вариант из исходного Excel-файла TechMES. Kp уменьшен относительно "
                + "классического правила, чтобы получить менее агрессивную реакцию.",
                "Kp = 0.33 * Ku",
                "Ti = Tu / 2",
                "Td = Tu / 3",
                "Внутренняя модификация исходного PID-калькулятора TechMES",
                null),

            PidTuneMethod.ClosedLoopTyreusLuybenPid => Method(
                "Tyreus-Luyben PID",
                "Закрытое правило по Ku и Tu. Обычно дает более робастные и менее агрессивные "
                + "настройки, чем классический Ziegler-Nichols.",
                "Kp = Ku / 2.2",
                "Ti = 2.2 * Tu",
                "Td = Tu / 6.3",
                "Сводная таблица Standard PID Tuning Methods, Michigan Tech",
                "https://pages.mtu.edu/~tbco/cm416/tuning_methods.pdf"),

            PidTuneMethod.ClosedLoopZieglerNicholsPid => Method(
                "Ziegler-Nichols PID по критическим колебаниям",
                "Классическое закрытое правило по критическому усилению Ku и периоду Tu. "
                + "Результат часто требует последующего уменьшения Kp на реальном объекте.",
                "Kp = 0.6 * Ku",
                "Ti = Tu / 2",
                "Td = Tu / 8",
                "J. G. Ziegler, N. B. Nichols, Optimum Settings for Automatic Controllers (1942)",
                "https://doi.org/10.1115/1.4019264"),

            _ => Method(
                "SIMC PI для FOPDT",
                "Простое робастное правило Skogestad. Tau c задает компромисс: большее значение "
                + "делает контур медленнее и устойчивее, меньшее - быстрее и чувствительнее.",
                "Kp = (1 / K) * Tau / (Tau c + Theta)",
                "Ti = min(Tau, 4 * (Tau c + Theta))",
                "Td = 0",
                "S. Skogestad, Simple Analytic Rules for Model Reduction and PID Controller Tuning",
                "https://doi.org/10.1016/S0959-1524(02)00062-8")
        };
    }

    /// <summary>
    /// Добавляет к справке рассчитанные значения и смысл коэффициентов регулятора.
    /// </summary>
    private static IReadOnlyList<PidTuneValueInfo> CreateResultValues(PidTuneCalculationResult result)
    {
        return new[]
        {
            Value("Kp", "Пропорциональный коэффициент: масштабирует текущую ошибку SP - PV.", result.Kp),
            Value("Ti", "Время интегрирования: определяет скорость устранения статической ошибки.", result.Ti, "с"),
            Value("Td", "Время дифференцирования: реагирует на скорость изменения ошибки.", result.Td, "с")
        };
    }

    private static PidTuneMethodInfo Method(
        string title,
        string description,
        string kp,
        string ti,
        string td,
        string sourceTitle,
        string? sourceUrl)
    {
        return new PidTuneMethodInfo(
            title,
            description,
            new[] { kp, ti, td },
            sourceTitle,
            sourceUrl);
    }

    private static PidTuneValueInfo Value(
        string symbol,
        string description,
        double? value,
        string? unit = null)
    {
        var formatted = value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "не задано";

        if (value.HasValue && !string.IsNullOrWhiteSpace(unit))
            formatted = $"{formatted} {unit}";

        return new PidTuneValueInfo(symbol, description, formatted);
    }
}

/// <summary>
/// Полное содержимое контекстной справки PID Tune.
/// </summary>
internal sealed record PidTuneInfoContent(
    PidTuneModelInfo Model,
    PidTuneMethodInfo Method,
    IReadOnlyList<PidTuneValueInfo> ResultValues,
    bool HasValidResult);

/// <summary>
/// Описание выбранной модели процесса.
/// </summary>
internal sealed record PidTuneModelInfo(
    string Title,
    string Equation,
    string Description,
    IReadOnlyList<PidTuneValueInfo> Values);

/// <summary>
/// Описание выбранного метода и формул расчета.
/// </summary>
internal sealed record PidTuneMethodInfo(
    string Title,
    string Description,
    IReadOnlyList<string> Formulas,
    string SourceTitle,
    string? SourceUrl);

/// <summary>
/// Значение и назначение одного коэффициента.
/// </summary>
internal sealed record PidTuneValueInfo(
    string Symbol,
    string Description,
    string CurrentValue);
