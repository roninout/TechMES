using System.Globalization;
using Microsoft.AspNetCore.Components;
using Radzen;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Contracts.Param.Tuning;
using TechMES.Web.Components.ParamWrite;
using TechMES.Web.Components.PidTune;
using TechMES.Web.Components.Trend;

namespace TechMES.Web.Components.Equipment;

/// <summary>Настройки, расчёты и диагностика PID Tune для панели оборудования.</summary>
public partial class EquipmentParamPanel
{
    /// <summary>
    /// Гарантирует наличие объекта настроек Tune даже до первой загрузки из Runtime.
    /// Это позволяет сразу показывать поля PV/SP и диапазоны, не плодя null-проверки в разметке.
    /// </summary>
    private ParamTuneSettingsResponse EnsureTuneSettings()
    {
        if (_tune is null)
        {
            _tune = new ParamTuneRuntimeResponse
            {
                EquipmentName = Equipment?.Name ?? "",
                TypeGroup = Equipment?.TypeGroup ?? EquipmentTypeGroup.Unknown,
                Supported = IsVgaEquipment,
                Settings = new ParamTuneSettingsResponse
                {
                    EquipmentName = Equipment?.Name ?? "",
                    PvMin = 0,
                    PvMax = 100,
                    SpMin = 0,
                    SpMax = 100
                }
            };
        }

        if (_tune.Settings is null)
            _tune.Settings = new ParamTuneSettingsResponse();

        _tune.Settings.EquipmentName = Equipment?.Name ?? _tune.Settings.EquipmentName;
        _tune.Settings.PvMin ??= 0;
        _tune.Settings.PvMax ??= 100;
        _tune.Settings.SpMin ??= 0;
        _tune.Settings.SpMax ??= 100;

        return _tune.Settings;
    }

    /// <summary>
    /// Имя трендового тега PV.
    ///
    /// Смена исходного сигнала инвалидирует не только Check,
    /// но и все ранее определенные коэффициенты модели.
    /// </summary>
    private string TunePv
    {
        get => EnsureTuneSettings().Pv ?? "";

        set
        {
            var settings = EnsureTuneSettings();
            var normalized = (value ?? "").Trim();

            if (string.Equals(settings.Pv ?? "", normalized, StringComparison.Ordinal))
            {
                return;
            }

            settings.Pv = normalized;
            settings.PvTrendFound = false;
            ResetTuneCalculationState(clearModelParameters: true);
            _tuneSettingsStatusText = "";
        }
    }

    /// <summary>
    /// Имя трендового тега SP.
    ///
    /// SP теперь является расчетным входом ClosedLoop:
    /// анализ выполняется по ошибке PV-SP.
    /// Поэтому смена SP инвалидирует предыдущий ClosedLoop-результат.
    /// </summary>
    private string TuneSp
    {
        get =>
            EnsureTuneSettings().Sp
            ?? "";

        set
        {
            var settings =
                EnsureTuneSettings();

            var normalized =
                (value ?? "").Trim();

            if (string.Equals(
                    settings.Sp ?? "",
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            settings.Sp =
                normalized;

            settings.SpTrendFound =
                false;

            ResetTuneCalculationState(
                clearModelParameters: true);

            _tuneSettingsStatusText = "";
        }
    }

    /// <summary>
    /// Имя online-тега фактического Kp для ClosedLoop.
    ///
    /// Test Kp не является трендовым тегом, но его изменение полностью
    /// инвалидирует прежний Ku/ClosedLoop результат.
    /// </summary>
    private string TuneTestKpTag
    {
        get =>
            EnsureTuneSettings().TestKpTag
            ?? "";

        set
        {
            var settings =
                EnsureTuneSettings();

            var normalized =
                (value ?? "").Trim();

            if (string.Equals(
                    settings.TestKpTag ?? "",
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            settings.TestKpTag =
                normalized;

            settings.TestKpFound =
                false;

            if (_tune is not null)
                _tune.TestKpValue = null;

            ResetTuneCalculationState(
                clearModelParameters: true);

            _tuneSettingsStatusText = "";
        }
    }

    private double? TunePvMin
    {
        get => EnsureTuneSettings().PvMin;
        set => EnsureTuneSettings().PvMin = value;
    }

    private double? TunePvMax
    {
        get => EnsureTuneSettings().PvMax;
        set => EnsureTuneSettings().PvMax = value;
    }

    private double? TuneSpMin
    {
        get => EnsureTuneSettings().SpMin;
        set => EnsureTuneSettings().SpMin = value;
    }

    private double? TuneSpMax
    {
        get => EnsureTuneSettings().SpMax;
        set => EnsureTuneSettings().SpMax = value;
    }

    /// <summary>
    /// Save относится к панели Settings, поэтому не зависит от того, выполнялся ли Calculate.
    /// PV/SP должны пройти Check. Test Kp необязателен, но если он введён, он тоже должен быть проверен.
    /// </summary>
    private bool CanSaveTune
    {
        get
        {
            var settings = _tune?.Settings;
            if (_isTuneSaving || settings is null || !settings.PvTrendFound || !settings.SpTrendFound)
                return false;

            return string.IsNullOrWhiteSpace(settings.TestKpTag) || settings.TestKpFound;
        }
    }

    /// <summary>
    /// FOPDT / Integrating:
    ///     требуют PV + OUT/ManTune.
    ///
    /// ClosedLoop:
    ///     требует PV + SP.
    ///     OUT не нужен; Ku берется из текущего online Test Kp.
    /// </summary>
    private bool CanCalculateTune
    {
        get
        {
            if (_isTuneCalculating
                || _tuneTrend is null)
            {
                return false;
            }

            var hasPv =
                _tuneTrend.Points.Any(
                    point =>
                        point.Series.Equals(
                            "Pv",
                            StringComparison.OrdinalIgnoreCase));

            if (!hasPv)
                return false;

            if (_tuneCalculationModel
                == TuneModelClosedLoop)
            {
                return _tuneTrend.Points.Any(
                    point =>
                        point.Series.Equals(
                            "Sp",
                            StringComparison.OrdinalIgnoreCase));
            }

            return _tuneTrend.Points.Any(
                point =>
                    point.Series.Equals(
                        "ManTune",
                        StringComparison.OrdinalIgnoreCase));
        }
    }


    /// <summary>
    /// Компактное отображение рассчитанных PID-значений.
    /// </summary>
    private static string FormatTuneNumber(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>
    /// Форматирует отношение 0..1 как проценты для status-строки.
    /// </summary>
    private static string FormatTunePercent(double? value)
    {
        return value.HasValue
            ? (value.Value * 100)
                .ToString(
                    "0.##",
                    CultureInfo.InvariantCulture)
              + "%"
            : "";
    }

    private IReadOnlyList<TuneOption> TuneMethodOptions => _tuneCalculationModel switch
    {
        TuneModelIntegrating => TuneIntegratingMethods,
        TuneModelClosedLoop => TuneClosedLoopMethods,
        _ => TuneFopdtMethods
    };

    private void NormalizeTuneCalculationMethod()
    {
        if (TuneMethodOptions.Any(item => item.Value == _tuneCalculationMethod))
            return;

        _tuneCalculationMethod = TuneMethodOptions[0].Value;
    }

    private PidTuneCalculationResult CalculateTune()
    {
        NormalizeTuneCalculationMethod();
        return PidTuneCalculator.Calculate(BuildTuneCalculationRequest());
    }

    /// <summary>
    /// Открывает подробную справку:
    /// - математическая модель;
    /// - фактический алгоритм анализа trend-точек;
    /// - все формулы идентификации;
    /// - acceptance thresholds;
    /// - diagnostics последнего Calculate;
    /// - формулы Kp/Ti/Td;
    /// - ссылки на первоисточники.
    /// </summary>
    private async Task OpenTuneInfoAsync()
    {
        NormalizeTuneCalculationMethod();

        var request =
            BuildTuneCalculationRequest();

        var result =
            PidTuneCalculator.Calculate(
                request);

        await DialogService.OpenAsync<PidTuneInfoDialog>(
            "Расчет PID-настроек",
            new Dictionary<string, object?>
            {
                [nameof(PidTuneInfoDialog.Request)] =
                    request,

                [nameof(PidTuneInfoDialog.Result)] =
                    result,

                [nameof(PidTuneInfoDialog.Identification)] =
                    _tuneIdentification
            },
            new DialogOptions
            {
                Width = "980px",
                Resizable = true,
                Draggable = true,
                CloseDialogOnOverlayClick = true,
                CloseDialogOnEsc = true
            });
    }

    /// <summary>
    /// true, когда текущий статус содержит классифицированную причину,
    /// для которой можно открыть диагностическую справку.
    /// </summary>
    private bool CanOpenTuneIssue =>
        _tuneIssueCode != PidTuneIssueCode.None &&
        !string.IsNullOrWhiteSpace(_tuneStatusText);

    /// <summary>
    /// Открывает модель-зависимую диагностическую справку.
    /// Диаграмма теперь различается для FOPDT, Integrating и ClosedLoop.
    /// </summary>
    private async Task OpenTuneIssueAsync()
    {
        if (!CanOpenTuneIssue)
            return;

        await DialogService.OpenAsync<PidTuneIssueDialog>(
            "Почему расчет невозможен",
            new Dictionary<string, object?>
            {
                [nameof(PidTuneIssueDialog.IssueCode)] =
                    _tuneIssueCode,

                [nameof(PidTuneIssueDialog.Message)] =
                    _tuneStatusText,

                [nameof(PidTuneIssueDialog.ProcessModel)] =
                    _tuneCalculationModel
            },
            new DialogOptions
            {
                Width = "900px",
                Resizable = true,
                Draggable = true,
                CloseDialogOnOverlayClick = true,
                CloseDialogOnEsc = true
            });
    }

    /// <summary>
    /// Собирает параметры формы Tune в DTO, который понимает общий PID-калькулятор.
    /// </summary>
    private PidTuneCalculationRequest BuildTuneCalculationRequest()
    {
        return new PidTuneCalculationRequest
        {
            ProcessModel = _tuneCalculationModel,
            TuneMethod = _tuneCalculationMethod,
            FopdtK = _tuneFopdtK,
            FopdtTau = _tuneFopdtTau,
            FopdtTheta = _tuneFopdtTheta,
            FopdtTauC = _tuneFopdtTauC,
            IntegratingKi = _tuneIntegratingKi,
            IntegratingTheta = _tuneIntegratingTheta,
            IntegratingTauC = _tuneIntegratingTauC,
            ClosedLoopKu = _tuneClosedKu,
            ClosedLoopTu = _tuneClosedTu
        };
    }

    /// <summary>
    /// Автоматически идентифицирует выбранную модель
    /// по фактически видимой области Tune-графика.
    ///
    /// FOPDT:
    ///     PV + OUT
    ///     -> whole-curve fit
    ///     -> K / Tau / Theta / TauC.
    ///
    /// Integrating:
    ///     PV + OUT
    ///     -> piecewise-linear fit
    ///     -> ki / Theta / TauC.
    ///
    /// ClosedLoop:
    ///     PV + SP + current online Test Kp
    ///     -> e(t)=PV-SP
    ///     -> устойчивые critical oscillations
    ///     -> Ku / Tu.
    /// </summary>
    private async Task CalculateTuneFromVisibleTrendAsync()
    {
        if (_tuneTrend is null)
            return;

        var selectedModel =
            _tuneCalculationModel;

        var selectedMethod =
            _tuneCalculationMethod;

        _isTuneCalculating = true;
        _tuneIssueCode =
            PidTuneIssueCode.None;

        try
        {
            var fromUtc =
                GetTuneVisibleFromUtc()
                ?? _tuneTrend.FromUtc;

            var toUtc =
                GetTuneVisibleToUtc()
                ?? _tuneTrend.ToUtc;

            var pv =
                GetTuneSamples(
                    "Pv",
                    fromUtc,
                    toUtc);

            IReadOnlyList<PidTuningSample> output =
                selectedModel == TuneModelClosedLoop
                    ? []
                    : GetTuneSamples(
                        "ManTune",
                        fromUtc,
                        toUtc);

            /*
             * SP нужен именно для ClosedLoop.
             * Анализ одного PV может принять периодическое изменение SP
             * за ultimate oscillation.
             */
            IReadOnlyList<PidTuningSample> sp =
                selectedModel == TuneModelClosedLoop
                    ? GetTuneSamples(
                        "Sp",
                        fromUtc,
                        toUtc)
                    : [];

            var identification =
                PidProcessIdentifier.Identify(
                    selectedModel,
                    pv,
                    output,
                    sp,
                    _tune?.TestKpValue);

            /*
             * Храним и успешный, и неуспешный результат:
             * Info-dialog сможет показать diagnostics последней попытки.
             */
            _tuneIdentification =
                identification;

            if (!identification.IsSuccess)
            {
                _tuneStatusStyle =
                    AlertStyle.Warning;

                _tuneStatusText =
                    identification.ErrorMessage;

                _tuneIssueCode =
                    identification.IssueCode
                    == PidTuneIssueCode.None
                        ? PidTuneIssueCode.Unknown
                        : identification.IssueCode;

                return;
            }

            switch (selectedModel)
            {
                case TuneModelFopdt:
                    _tuneFopdtK =
                        identification.K;

                    _tuneFopdtTau =
                        identification.Tau;

                    _tuneFopdtTheta =
                        identification.Theta;

                    _tuneFopdtTauC =
                        identification.TauC;
                    break;

                case TuneModelIntegrating:
                    _tuneIntegratingKi =
                        identification.Ki;

                    _tuneIntegratingTheta =
                        identification.Theta;

                    _tuneIntegratingTauC =
                        identification.TauC;
                    break;

                case TuneModelClosedLoop:
                    _tuneClosedKu =
                        identification.Ku;

                    _tuneClosedTu =
                        identification.Tu;
                    break;
            }

            /*
             * Асинхронная операция не должна менять выбранные
             * пользователем model/method.
             */
            _tuneCalculationModel =
                selectedModel;

            _tuneCalculationMethod =
                selectedMethod;

            var selectedTune =
                CalculateTune();

            if (!selectedTune.IsValid)
            {
                _tuneStatusStyle =
                    AlertStyle.Warning;

                _tuneStatusText =
                    selectedTune.Message;

                _tuneIssueCode =
                    PidTuneIssueCode.InvalidControllerParameters;

                return;
            }

            _tuneStatusStyle =
                AlertStyle.Success;

            _tuneIssueCode =
                PidTuneIssueCode.None;

            _tuneStatusText =
                BuildTuneCalculationStatus(
                    selectedModel,
                    selectedTune,
                    identification);

            NotificationService.Notify(
                new NotificationMessage
                {
                    Severity =
                        NotificationSeverity.Success,

                    Summary =
                        "PID Tune",

                    Detail =
                        "Calculation completed.",

                    Duration =
                        2500
                });
        }
        finally
        {
            _isTuneCalculating = false;

            if (!_isDisposed)
                await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Формирует краткий status с реальными quality metrics.
    /// Полный математический разбор остается в Info-dialog.
    /// </summary>
    private string BuildTuneCalculationStatus(string processModel,PidTuneCalculationResult tuneResult,PidProcessIdentificationResult identification)
    {
        var modelParameters =
            processModel switch
            {
                TuneModelIntegrating =>
                    $"ki={FormatTuneNumber(_tuneIntegratingKi)}, "
                    + $"Theta={FormatTuneNumber(_tuneIntegratingTheta)}, "
                    + $"Tau c={FormatTuneNumber(_tuneIntegratingTauC)}",

                TuneModelClosedLoop =>
                    $"Ku={FormatTuneNumber(_tuneClosedKu)}, "
                    + $"Tu={FormatTuneNumber(_tuneClosedTu)}",

                _ =>
                    $"K={FormatTuneNumber(_tuneFopdtK)}, "
                    + $"Tau={FormatTuneNumber(_tuneFopdtTau)}, "
                    + $"Theta={FormatTuneNumber(_tuneFopdtTheta)}, "
                    + $"Tau c={FormatTuneNumber(_tuneFopdtTauC)}"
            };

        var controllerParameters =
            $"Kp={FormatTuneNumber(tuneResult.Kp)}, "
            + $"Ti={FormatTuneNumber(tuneResult.Ti)}, "
            + $"Td={FormatTuneNumber(tuneResult.Td)}";

        var quality =
            processModel switch
            {
                TuneModelClosedLoop =>
                    $"A={FormatTuneNumber(identification.OscillationAmplitude)}, "
                    + $"period CV={FormatTunePercent(identification.PeriodCv)}, "
                    + $"amplitude CV={FormatTunePercent(identification.AmplitudeCv)}, "
                    + $"amplitude ratio={FormatTuneNumber(identification.AmplitudeTrendRatio)}, "
                    + $"SP variation={FormatTunePercent(identification.SetpointVariationRatio)}, "
                    + $"SP drift={FormatTunePercent(identification.SetpointDriftRatio)}, "
                    + $"cycles={identification.CyclesUsed}",

                TuneModelIntegrating =>
                    $"R²={FormatTuneNumber(identification.R2)}, "
                    + $"RMSE={FormatTuneNumber(identification.Rmse)}, "
                    + $"OUT tail={FormatTunePercent(identification.OutputTailRangeRatio)}",

                _ =>
                    $"R²={FormatTuneNumber(identification.R2)}, "
                    + $"RMSE={FormatTuneNumber(identification.Rmse)}, "
                    + $"observed={FormatTunePercent(identification.ObservedResponseFraction)}, "
                    + $"OUT tail={FormatTunePercent(identification.OutputTailRangeRatio)}"
            };

        var testKpNote =
            processModel == TuneModelClosedLoop
                ? IsTuneLive
                    ? $" Ku uses current online Test Kp={FormatTuneNumber(_tune?.TestKpValue)}."
                    : $" Historical window: Ku uses CURRENT online Test Kp={FormatTuneNumber(_tune?.TestKpValue)}; verify that this Kp was active during the selected interval."
                : "";

        return
            $"Calculation completed. "
            + $"{modelParameters}, "
            + $"{controllerParameters}, "
            + $"{quality}, "
            + $"method={tuneResult.Message}, "
            + $"points={identification.PointsUsed}."
            + testKpNote;
    }


    /// <summary>
    /// Достает точки выбранной серии из текущего видимого окна Tune-графика.
    /// Для расчета используется RawValue, потому Value уже масштабирован в общую ось 0..100.
    /// </summary>
    private IReadOnlyList<PidTuningSample> GetTuneSamples(string seriesName, DateTime fromUtc, DateTime toUtc)
    {
        var normalizedFromUtc = NormalizeTrendPointUtc(fromUtc);
        var normalizedToUtc = NormalizeTrendPointUtc(toUtc);

        return _tuneTrend?.Points
            .Where(point => point.Series.Equals(seriesName, StringComparison.OrdinalIgnoreCase))
            .Select(point => new
            {
                TimeUtc = NormalizeTrendPointUtc(point.Time),
                point.RawValue
            })
            .Where(point => point.TimeUtc >= normalizedFromUtc
                            && point.TimeUtc <= normalizedToUtc
                            && !double.IsNaN(point.RawValue)
                            && !double.IsInfinity(point.RawValue))
            .OrderBy(point => point.TimeUtc)
            .Select(point => new PidTuningSample(point.TimeUtc, point.RawValue))
            .ToList()
            ?? [];
    }

    /// <summary>
    /// Приводит DateTime из DTO к UTC так же, как это делает ECharts-компонент.
    /// </summary>
    private static DateTime NormalizeTrendPointUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };
    }

    /// <summary>
    /// Загружает сохраненные настройки PID Tune, текущие значения ManTune/PV/SP и line-тренды.
    /// </summary>
    private async Task LoadTuneAsync(string equipmentName, CancellationToken ct)
    {
        var draftSettings = _hasTuneSettingsLoadedFromStore
            && _tune?.Settings is not null
            && string.Equals(_tune.Settings.EquipmentName, equipmentName, StringComparison.OrdinalIgnoreCase)
                ? _tune.Settings
                : null;

        var runtimeTune = await ParamApi.GetTuneAsync(
            equipmentName,
            _trendHistoryMinutes,
            null,
            null,
            ct,
            draftSettings);

        if (draftSettings is not null)
            runtimeTune.Settings = draftSettings;
        else
            _hasTuneSettingsLoadedFromStore = true;

        _tune = runtimeTune;
        _tuneTrend = runtimeTune.Trend;
        _tuneVisibleFromUtc ??= runtimeTune.Trend?.ToUtc.AddMinutes(-_windowMinutes);
        _tuneVisibleToUtc ??= runtimeTune.Trend?.ToUtc;

        if (!string.IsNullOrWhiteSpace(runtimeTune.Message))
            _tuneStatusStyle = runtimeTune.Supported ? AlertStyle.Info : AlertStyle.Warning;
    }

    /// <summary>
    /// Проверяет PID Tune-теги одной операцией.
    ///
    /// PV/SP:
    /// выполняется общая numeric tag проверка с RequireTrend = true.
    ///
    /// Test Kp:
    /// выполняется та же общая проверка с RequireTrend = false.
    ///
    /// То есть Tune больше не имеет собственного механизма TagRead.
    /// Он является только потребителем общего Param Runtime API.
    /// </summary>
    private async Task CheckTuneTagsAsync()
    {
        if (Equipment is null)
            return;

        var settings = EnsureTuneSettings();

        var pvTag = (settings.Pv ?? "").Trim();
        var spTag = (settings.Sp ?? "").Trim();
        var testKpTag = (settings.TestKpTag ?? "").Trim();

        if (string.IsNullOrWhiteSpace(pvTag) || string.IsNullOrWhiteSpace(spTag))
        {
            _tuneSettingsStatusText = string.IsNullOrWhiteSpace(pvTag) && string.IsNullOrWhiteSpace(spTag)
                ? "Enter PV and SP trend tags."
                : string.IsNullOrWhiteSpace(pvTag)
                    ? "Enter PV trend tag."
                    : "Enter SP trend tag.";

            return;
        }

        var testKpRequired = _tuneCalculationModel == TuneModelClosedLoop || !string.IsNullOrWhiteSpace(testKpTag);

        if (_tuneCalculationModel == TuneModelClosedLoop && string.IsNullOrWhiteSpace(testKpTag))
        {
            settings.TestKpFound = false;

            if (_tune is not null)
                _tune.TestKpValue = null;

            _tuneSettingsStatusText = "Enter Test Kp online tag for ClosedLoop.";
            return;
        }

        _isTuneChecking = true;
        _tuneSettingsStatusText = testKpRequired
            ? "Checking PV/SP trends and Test Kp online tag..."
            : "Checking PV and SP trend tags...";

        try
        {
            var pvResult = await ParamApi.CheckNumericTagAsync(new ParamTagCheckRequest
            {
                TagName = pvTag,
                RequireTrend = true
            }, GetActiveLoadToken());

            var spResult = await ParamApi.CheckNumericTagAsync(new ParamTagCheckRequest
            {
                TagName = spTag,
                RequireTrend = true
            }, GetActiveLoadToken());

            ParamTagCheckResponse? testKpResult = null;

            if (testKpRequired)
            {
                testKpResult = await ParamApi.CheckNumericTagAsync(new ParamTagCheckRequest
                {
                    TagName = testKpTag,
                    RequireTrend = false
                }, GetActiveLoadToken());
            }

            settings.Pv = pvResult.TagName;
            settings.PvTrendFound = pvResult.Found;

            settings.Sp = spResult.TagName;
            settings.SpTrendFound = spResult.Found;

            if (testKpResult is not null)
            {
                settings.TestKpTag = testKpResult.TagName;
                settings.TestKpFound = testKpResult.Found;

                if (_tune is not null)
                    _tune.TestKpValue = testKpResult.CurrentValue;
            }
            else
            {
                settings.TestKpFound = false;

                if (string.IsNullOrWhiteSpace(settings.TestKpTag) && _tune is not null)
                    _tune.TestKpValue = null;
            }

            _tuneSettingsStatusText = BuildTuneTagCheckStatus(pvResult, spResult, testKpResult);

            // После Check перечитываем Runtime один раз.
            // График получает проверенные PV/SP,
            // а Test Kp получает актуальное online-значение.
            await LoadTuneAsync(Equipment.Name, GetActiveLoadToken());
        }
        catch (OperationCanceledException) when (_isDisposed || !IsActive)
        {
        }
        catch (Exception ex)
        {
            _tuneSettingsStatusText = "Cannot check PID Tune tags: " + ex.Message;

            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Error,
                Summary = "PID Tune",
                Detail = "Cannot check PID Tune tags.",
                Duration = 3500
            });
        }
        finally
        {
            _isTuneChecking = false;

            if (!_isDisposed)
                StateHasChanged();
        }
    }

    /// <summary>
    /// Формирует единый статус PV/SP/Test Kp Check.
    /// </summary>
    private static string BuildTuneTagCheckStatus(ParamTagCheckResponse pvResult, ParamTagCheckResponse spResult, ParamTagCheckResponse? testKpResult)
    {
        var allFound = pvResult.Found && spResult.Found && (testKpResult is null || testKpResult.Found);

        if (allFound)
        {
            return testKpResult is null
                ? "PV and SP trend tags found."
                : "PV/SP trend tags and Test Kp online tag found.";
        }

        var problems = new List<string>();

        AddTuneTagProblem(problems, "PV", pvResult);
        AddTuneTagProblem(problems, "SP", spResult);

        if (testKpResult is not null)
            AddTuneTagProblem(problems, "Test Kp", testKpResult);

        return string.Join(" ", problems);
    }

    /// <summary>
    /// Добавляет причину ошибки одного Tune-тега.
    ///
    /// Метод получает уже общий ParamTagCheckResponse.
    /// Tune здесь только интерпретирует общий результат для своего UI.
    /// </summary>
    private static void AddTuneTagProblem(ICollection<string> problems, string role, ParamTagCheckResponse result)
    {
        if (result.Found)
            return;

        var tagName = string.IsNullOrWhiteSpace(result.TagName) ? "(empty)" : result.TagName;

        string reason;

        if (!result.TrendRequired)
        {
            reason = result.CurrentValue.HasValue
                ? "online validation failed"
                : "numeric online value was not read";
        }
        else
        {
            reason = result.TrendFound
                ? "numeric value was not read"
                : result.CurrentValue.HasValue
                    ? "trend reference was not found"
                    : "tag was not read and trend reference was not found";
        }

        problems.Add($"{role} tag '{tagName}': {reason}.");
    }

    /// <summary>
    /// Сохраняет проверенные Tune-теги и диапазоны независимо от наличия Calculate.
    ///
    /// Новые Kp/Ti/Td сохраняются только тогда, когда существует успешная
    /// автоматическая идентификация именно текущей модели.
    ///
    /// Для ClosedLoop дополнительно проверяется, что online Test Kp
    /// не изменился после последнего Calculate.
    /// </summary>
    private async Task SaveTuneAsync()
    {
        if (Equipment is null || !CanSaveTune)
            return;

        var settings = EnsureTuneSettings();

        var hasCurrentIdentification =
            _tuneIdentification?.IsSuccess == true &&
            string.Equals(_tuneIdentification.ProcessModel, _tuneCalculationModel, StringComparison.Ordinal);

        /*
         * Для ClosedLoop Ku равен Test Kp в момент Calculate.
         * Если online Kp после расчета изменился, старый Ku уже нельзя
         * считать актуальным результатом текущего испытания.
         */
        if (hasCurrentIdentification && _tuneCalculationModel == TuneModelClosedLoop)
        {
            var identifiedKu = _tuneIdentification?.Ku;
            var currentTestKp = _tune?.TestKpValue;

            hasCurrentIdentification =
                identifiedKu.HasValue &&
                currentTestKp.HasValue &&
                Math.Abs(identifiedKu.Value - currentTestKp.Value) <=
                Math.Max(1e-9, Math.Abs(currentTestKp.Value) * 1e-9);
        }

        var tuneResult = hasCurrentIdentification
            ? CalculateTune()
            : PidTuneCalculationResult.Invalid("No current PID Tune calculation.");

        /*
         * Если Calculate после изменения исходных условий не выполнялся,
         * сохраняем только Settings, а последние Kp/Ti/Td в БД не трогаем.
         */
        var kp = tuneResult.IsValid ? tuneResult.Kp : settings.Kp;
        var ti = tuneResult.IsValid ? tuneResult.Ti : settings.Ti;
        var td = tuneResult.IsValid ? tuneResult.Td : settings.Td;

        // Check-флаги являются только Runtime/UI-состоянием и в БД не хранятся.
        var pvTrendFound = settings.PvTrendFound;
        var spTrendFound = settings.SpTrendFound;
        var testKpFound = settings.TestKpFound;

        _isTuneSaving = true;

        try
        {
            var saved = await ParamApi.SaveTuneAsync(Equipment.Name, new ParamTuneSaveRequest
            {
                Pv = settings.Pv,
                PvMin = settings.PvMin,
                PvMax = settings.PvMax,
                Sp = settings.Sp,
                SpMin = settings.SpMin,
                SpMax = settings.SpMax,
                TestKpTag = settings.TestKpTag,
                Kp = kp,
                Ti = ti,
                Td = td
            }, GetActiveLoadToken());

            /*
             * Store возвращает persistent-поля.
             * Восстанавливаем результат текущего Check.
             */
            saved.PvTrendFound = pvTrendFound;
            saved.SpTrendFound = spTrendFound;
            saved.TestKpFound = testKpFound;

            _tune ??= new ParamTuneRuntimeResponse
            {
                EquipmentName = Equipment.Name,
                TypeGroup = Equipment.TypeGroup,
                Supported = true
            };

            _tune.Settings = saved;

            _tuneSettingsStatusText = tuneResult.IsValid
                ? "PID Tune settings and calculated coefficients saved."
                : "PID Tune settings saved. Calculated coefficients were not changed.";

            await LoadTuneAsync(Equipment.Name, GetActiveLoadToken());

            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Success,
                Summary = "PID Tune",
                Detail = _tuneSettingsStatusText,
                Duration = 2500
            });
        }
        catch (OperationCanceledException) when (_isDisposed || !IsActive)
        {
        }
        catch (Exception ex)
        {
            _tuneSettingsStatusText = "Cannot save Tune settings: " + ex.Message;

            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Error,
                Summary = "PID Tune",
                Detail = "Cannot save Tune settings.",
                Duration = 3500
            });
        }
        finally
        {
            _isTuneSaving = false;

            if (!_isDisposed)
                StateHasChanged();
        }
    }
}
