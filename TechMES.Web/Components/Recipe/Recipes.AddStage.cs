using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    // Current проверяем по всем Unit: рецепт может использоваться не только выбранным Unit.
    private bool IsCurrentRecipeStructureLocked => _snapshot is not null && IsCurrentRecipe(_snapshot.Recipe.Id);

    // Для Current каталог стадий не отображаем.
    private bool ShowStageTemplateList => EditorMode && !IsCurrentRecipeStructureLocked;

    // Добавление, удаление и перенос меняют позиции стадий и адреса PLC.
    private bool CanAddStage => EditorMode && CanManageRecipe && !_dialogOpen && !IsCurrentRecipeStructureLocked;

    /// <summary>Добавляет локальную стадию; новые параметры инициализируются нулями.</summary>
    private async Task AddStageAsync(int templateId, int? beforeStageId)
    {
        if (!CanAddStage || _disposed || _snapshot is null) return;

        var template = _stageDefinitions.FirstOrDefault(item => item.Id == templateId);

        if (template is null) return;

        _changingStage = true;

        try
        {
            var ids = template.Slots.Where(id => id.HasValue).Select(id => id!.Value).Concat(template.Overflow).Distinct().ToList();
            var definitions = _parameterCatalog.Parameters.Where(item => item.BaseRecipeId == template.FamilyId).ToDictionary(item => item.Id);

            if (ids.Any(id => !definitions.ContainsKey(id)))
                throw new InvalidOperationException("Template metadata is incomplete. Press Refresh.");

            var prefix = await FindDraftPrefixAsync();

            if (_disposed) return;

            var stage = new RecipeStageDto { Id = _nextDraftStageId--, Name = template.Name, BaseStageId = template.Id, TagPrefix = prefix };

            foreach (var id in ids)
                stage.Parameters.Add(id, new() { Id = 0, Value = 0, Active = true });

            var index = beforeStageId.HasValue ? _snapshot.Stages.FindIndex(item => item.Id == beforeStageId) : _snapshot.Stages.Count;

            if (index < 0) return;

            _snapshot.Stages.Insert(index, stage);

            RebuildDraft();
            RestoreStageSelection(stage.Id);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed) _error = ex.Message;
        }
        finally
        {
            _changingStage = false;
        }
    }

    /// <summary>Показывает предварительный prefix; окончательное назначение проверяет сервер при Save.</summary>
    private async Task<string> FindDraftPrefixAsync()
    {
        var prefixes = (_savedSnapshot?.Stages ?? []).Select(item => item.TagPrefix.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToList();

        if (prefixes.Count > 0)
            return prefixes.Count == 1 ? prefixes[0] : "";

        if (_snapshot is null || _snapshot.Recipe.Id == _stageTemplates.BaseRecipeId || SelectedPage is not { } page || page.BaseRecipeId != _stageTemplates.BaseRecipeId || page.RecipeId == _snapshot.Recipe.Id)
            return "";

        var source = await RecipeApi.GetAsync(page.RecipeId, _stop.Token);

        prefixes = source.Stages.Select(item => item.TagPrefix.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToList();

        return prefixes.Count == 1 ? prefixes[0] : "";
    }
}