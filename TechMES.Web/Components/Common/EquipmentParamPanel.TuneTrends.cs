using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

public partial class EquipmentParamPanel
{
    /// <summary>
    /// Создаёт логические серии Tune из текущих полей Settings.
    /// Новый тег или введённый диапазон меняет конфигурацию при следующем рендере.
    /// </summary>
    private IReadOnlyList<ScadaTrendSeries> BuildTuneRadzenSeries()
    {
        if (Equipment is null || _tune?.Supported != true)
            return [];

        var settings = EnsureTuneSettings();

        var series = new List<ScadaTrendSeries>
        {
            new()
            {
                TagName = "ManTune",
                SourceTagName = $"{Equipment.Name}.ManTune",
                Title = "Out",
                Color = "#4F81BD",
                Style = ScadaTrendStyle.Line,
                SourceMinimum = 0,
                SourceMaximum = 100
            }
        };

        if (!string.IsNullOrWhiteSpace(settings.Sp))
        {
            series.Add(new ScadaTrendSeries
            {
                TagName = "Sp",
                SourceTagName = settings.Sp.Trim(),
                Title = "Sp",
                Color = "#F59E0B",
                Style = ScadaTrendStyle.Line,
                SourceMinimum = settings.SpMin ?? 0,
                SourceMaximum = settings.SpMax ?? 100
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.Pv))
        {
            series.Add(new ScadaTrendSeries
            {
                TagName = "Pv",
                SourceTagName = settings.Pv.Trim(),
                Title = "Pv",
                Color = "#2E7D32",
                Style = ScadaTrendStyle.Line,
                SourceMinimum = settings.PvMin ?? 0,
                SourceMaximum = settings.PvMax ?? 100
            });
        }

        return series;
    }

    /// <summary>
    /// Загружает тренды Tune за один временной блок. Текущие значения при таком
    /// запросе не читаются: панель получает их отдельно обычным способом.
    /// </summary>
    private async Task<ParamTrendResponse> LoadTuneChartHistoryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (Equipment is null)
            return new ParamTrendResponse();

        var settings = EnsureTuneSettings();

        var requestSettings = new ParamTuneSettingsResponse
        {
            EquipmentName = Equipment.Name,
            Pv = settings.Pv,
            PvMin = settings.PvMin,
            PvMax = settings.PvMax,
            Sp = settings.Sp,
            SpMin = settings.SpMin,
            SpMax = settings.SpMax
        };

        var response = await ParamApi.GetTuneAsync(
            Equipment.Name,
            (int)Math.Ceiling((toUtc - fromUtc).TotalMinutes),
            fromUtc,
            toUtc,
            ct,
            requestSettings,
            historyOnly: true);

        return response.Trend;
    }

    /// <summary>
    /// После загрузки или изменения окна обновляет доступность Calculate.
    /// </summary>
    private Task OnTuneRadzenSelectionChanged()
    {
        return InvokeAsync(StateHasChanged);
    }
}