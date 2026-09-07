using Radzen;
using TechMES.Contracts.Calc;
using TechMES.Web.Components.Calc.Formula;

namespace TechMES.Web.Components.Pages;

public partial class Calculation
{
    private List<CalcModelDto> _catalogModels = [];
    private Dictionary<CalcModelDto, CalcJobDto> _formulaJobsByModel = [];
    private CalcModelDto? _newFormulaModel;
    private FormulaConfigurationPanel? _formulaConfigurationPanel;

    private bool FormulaBusy => _selectedModel?.Type == CalcModelTypeDto.Formula && _formulaConfigurationPanel?.IsBusy == true;

    /// <summary>
    /// SCADA models связываются с Job по EquipmentName, Formula — по Job.Id.
    /// Формулы добавляются только в список WEB, без изменения CtApi Catalog.
    /// </summary>
    private void RebuildJobAndModelIndexes(IReadOnlyList<CalcJobDto> jobs)
    {
        _jobsByEquipment = jobs.Where(job => !IsFormulaJob(job) && !string.IsNullOrWhiteSpace(job.EquipmentName))
            .GroupBy(job => job.EquipmentName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.Id).First(), StringComparer.OrdinalIgnoreCase);

        _formulaJobsByModel = [];

        foreach (var job in jobs.Where(IsFormulaJob).OrderBy(job => job.SortOrder).ThenBy(job => job.Id))
        {
            var model = new CalcModelDto
            {
                Name = job.Name,
                Description = job.Description ?? "",
                Station = "",
                Type = CalcModelTypeDto.Formula,
                ItemTags = []
            };

            _formulaJobsByModel.Add(model, job);
        }

        _allModels = [.. _catalogModels, .. _formulaJobsByModel.Keys];

        if (_newFormulaModel is not null)
            _allModels.Add(_newFormulaModel);
    }

    private static bool IsFormulaJob(CalcJobDto job) => string.Equals(job.DefinitionCode?.Trim(), "formula.expression", StringComparison.OrdinalIgnoreCase);

    private string GetModelKey(CalcModelDto model)
    {
        if (ReferenceEquals(model, _newFormulaModel))
            return "formula:new";

        return _formulaJobsByModel.TryGetValue(model, out var job) ? $"formula:{job.Id}" : $"model:{model.Name}";
    }

    private async Task BeginNewFormulaAsync()
    {
        if (!await ConfirmDiscardAsync()) return;

        _newFormulaModel = new CalcModelDto
        {
            Name = "New formula",
            Description = "Mathematical calculation",
            Station = "",
            Type = CalcModelTypeDto.Formula,
            ItemTags = []
        };

        _selectedStation = AllStationsText;
        _selectedType = CalcModelTypeDto.Formula.ToString();
        _searchText = "";

        RebuildJobAndModelIndexes(_jobsByEquipment.Values.Concat(_formulaJobsByModel.Values).ToArray());
        BuildFilterItems();
        ApplyFilters();

        _selectedModel = _newFormulaModel;
    }

    private Task ReloadAfterFormulaJobChangedAsync(long jobId)
    {
        return ReloadJobsAndStatesAsync(selectFormulaJobId: jobId);
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (FormulaBusy) return false;

        // @ref предыдущего компонента может остаться после переключения типа.
        // Проверяем только панель, которая сейчас действительно открыта.
        var pending = _selectedModel?.Type switch
        {
            CalcModelTypeDto.Tank => _tankConfigurationPanel?.HasPendingChanges == true,
            CalcModelTypeDto.Density => _densityConfigurationPanel?.HasPendingChanges == true,
            CalcModelTypeDto.Capacity => _capacityConfigurationPanel?.HasPendingChanges == true,
            CalcModelTypeDto.Content => _contentConfigurationPanel?.HasPendingChanges == true,
            CalcModelTypeDto.Formula => _formulaConfigurationPanel?.HasPendingChanges == true,
            _ => false
        };

        return !pending || await DialogService.Confirm("The current calculation contains unsaved changes. Discard them and continue?", "Unsaved changes", new ConfirmOptions { OkButtonText = "Discard", CancelButtonText = "Stay" }) == true;
    }
}