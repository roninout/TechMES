using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Routing;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

/// <summary>Черновик редактора существует в памяти до общей кнопки Save.</summary>
public partial class Recipes
{
    private RecipeSnapshotDto? _savedSnapshot;
    private RecipeBaseParameterCatalogDto _parameterCatalog = new();
    private List<RecipeBaseStageDto> _stageDefinitions = [];
    private bool _savingDraft, _uncertainDraft, _hasRecipeChanges;
    private string _savedRecipeStamp = "";
    private int _nextDraftStageId = -1, _selectorRevision;

    private bool HasRecipeChanges => EditorMode && _snapshot is not null && _savedSnapshot is not null && _hasRecipeChanges;
    private bool CanSaveRecipe => CanManageRecipe && HasRecipeChanges;

    /// <summary>Отделяет исходный снимок от редактируемых объектов.</summary>
    private static RecipeSnapshotDto CloneRecipe(RecipeSnapshotDto value) => JsonSerializer.Deserialize<RecipeSnapshotDto>(JsonSerializer.Serialize(value))!;

    /// <summary>Пересчитывает изменения после правки, а не на каждом drag-событии.</summary>
    private void MarkRecipeChanges() => _hasRecipeChanges = _snapshot is not null && JsonSerializer.Serialize(_snapshot) != _savedRecipeStamp;

    /// <summary>Обновляет порядок и только используемые колонки после изменения состава.</summary>
    private void RebuildDraft()
    {
        if (_snapshot is null) return;

        for (var i = 0; i < _snapshot.Stages.Count; i++)
            _snapshot.Stages[i].Number = i + 1;

        _snapshot.Stages = _snapshot.Stages.ToList();

        var used = _snapshot.Stages.SelectMany(item => item.Parameters.Keys).ToHashSet();
        var columns = _snapshot.Columns.ToDictionary(item => item.Id);

        foreach (var parameter in _parameterCatalog.Parameters.Where(item => used.Contains(item.Id)))
            columns.TryAdd(parameter.Id, new() { Id = parameter.Id, Name = parameter.Name, Description = parameter.Description, Order = parameter.Order, TagSuffix = parameter.Tag, Kind = parameter.Kind, Format = parameter.Format });

        _snapshot.Columns = columns.Values.Where(item => used.Contains(item.Id)).OrderBy(item => item.Order).ThenBy(item => item.Name).ThenBy(item => item.Id).ToList();

        MarkRecipeChanges();
    }

    /// <summary>Отмечает шаблон, присутствующий в текущем черновике.</summary>
    private bool IsStageTemplateUsed(int templateId) => Stages.Any(item => item.BaseStageId == templateId);

    /// <summary>Предупреждает об отказе от несохранённых изменений.</summary>
    private async Task<bool> DiscardRecipeAsync()
    {
        if (!HasRecipeChanges) return true;

        _dialogOpen = true;

        try
        {
            return await DialogService.Confirm("Discard unsaved recipe changes?", "Unsaved changes", new ConfirmOptions { OkButtonText = "Discard", CancelButtonText = "Stay" }) == true && !_disposed;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Защищает Back, Parameters, Stages и другие внутренние ссылки.</summary>
    private async Task BeforeRecipeNavigationAsync(LocationChangingContext context)
    {
        if (IsBusy || _dialogOpen || !await DiscardRecipeAsync())
            context.PreventNavigation();
    }

    /// <summary>Обработчик общей кнопки Save.</summary>
    private async Task SaveRecipeAsync()
    {
        await SaveRecipeDraftAsync();
    }

    /// <summary>Сохраняет пакет один раз и принимает фактические ID из ответа сервера.</summary>
    private async Task<bool> SaveRecipeDraftAsync()
    {
        if (!CanSaveRecipe || _snapshot is null || _savedSnapshot is null)
            return !HasRecipeChanges;

        var ct = _stop.Token;
        var request = new RecipeEditorSaveRequest { Original = CloneRecipe(_savedSnapshot), Value = CloneRecipe(_snapshot), RecipePageId = SelectedPage?.Id };

        _savingDraft = true;
        _error = null;

        try
        {
            var result = await RecipeApi.SaveEditorAsync(_snapshot.Recipe.Id, request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.Snapshot is null)
                throw new InvalidOperationException("Unexpected recipe save response.");

            var selectedIndex = SelectedStageIndex;

            _snapshot = result.Snapshot;
            _savedSnapshot = CloneRecipe(_snapshot);
            _savedRecipeStamp = JsonSerializer.Serialize(_savedSnapshot);
            _hasRecipeChanges = false;
            _nextDraftStageId = -1;
            _uncertainDraft = false;

            var header = _catalog.Recipes.FirstOrDefault(item => item.Id == _snapshot.Recipe.Id);

            if (header is not null)
                header.Name = _snapshot.Recipe.Name;

            _selectorRevision++;

            RestoreStageSelection(_snapshot.Stages.ElementAtOrDefault(Math.Max(0, selectedIndex))?.Id);
            ClearStageDrag();

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Recipe saved to database.", 2500);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _uncertainDraft = ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = _uncertainDraft ? "Save result is uncertain. Press Refresh before editing again. " + ex.Message : ex.Message;
            }

            return false;
        }
        finally
        {
            _savingDraft = false;
        }
    }

    /// <summary>Перед New/Copy предлагает сохранить текущий изменённый рецепт.</summary>
    private async Task<bool> SaveBeforeManagementAsync()
    {
        if (!HasRecipeChanges) return true;

        _dialogOpen = true;
        bool accepted;

        try
        {
            accepted = await DialogService.Confirm("Save recipe changes before continuing?", "Unsaved changes", new ConfirmOptions { OkButtonText = "Save", CancelButtonText = "Cancel" }) == true;
        }
        finally
        {
            _dialogOpen = false;
        }

        return accepted && await SaveRecipeDraftAsync();
    }
}