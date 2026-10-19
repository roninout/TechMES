using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Добавление стадии из шаблона семейства выбранного рецепта.
/// </summary>
public partial class Recipes
{
    private bool CanAddStage => CanManageRecipe && _editValues;

    /// <summary>
    /// Предлагает префикс только при наличии одного непустого варианта.
    /// Имя Unit и код шаблона не преобразуются в префикс автоматически.
    /// </summary>
    private static string FindStageTagPrefix(RecipeSnapshotDto snapshot)
    {
        var prefixes = snapshot.Stages.Select(stage => stage.TagPrefix.Trim()).Where(prefix => prefix.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToList();

        return prefixes.Count == 1 ? prefixes[0] : "";
    }

    /// <summary>
    /// Загружает шаблоны, открывает диалог и добавляет стадию с последующим перечитыванием.
    /// </summary>
    private async Task AddStageAsync()
    {
        if (!CanAddStage || _disposed || _snapshot is null)
            return;

        var recipe = _snapshot;
        var recipeId = recipe.Recipe.Id;
        var page = SelectedPage;
        var ct = _stop.Token;
        var expectedStages = recipe.Stages.Select(stage => new RecipeStagePositionDto { Id = stage.Id, Number = stage.Number, Auto = stage.Auto }).ToList();
        var requestStarted = false;
        var saved = false;

        _changingStage = true;
        _loading = true;
        _error = null;

        try
        {
            var catalog = await RecipeApi.GetStageTemplatesAsync(recipeId, ct);
            ct.ThrowIfCancellationRequested();

            if (catalog.Templates.Count == 0)
            {
                NotificationService.Notify(NotificationSeverity.Warning, "Recipe", "No stage templates are available for this recipe family.", 4500);
                return;
            }

            var prefix = FindStageTagPrefix(recipe);

            // Для пустого рецепта используем текущий рецепт только выбранного Unit того же семейства.
            // При нескольких разных префиксах в выбранном рецепте оставляем ручной ввод.
            if (!recipe.Stages.Any(stage => !string.IsNullOrWhiteSpace(stage.TagPrefix)) && page is not null && page.BaseRecipeId == catalog.BaseRecipeId && page.RecipeId > 0 && page.RecipeId != recipeId)
            {
                var currentRecipe = await RecipeApi.GetAsync(page.RecipeId, ct);
                ct.ThrowIfCancellationRequested();

                prefix = FindStageTagPrefix(currentRecipe);
            }

            _loading = false;
            await InvokeAsync(StateHasChanged);

            _dialogOpen = true;

            var input = await DialogService.OpenAsync<RecipeStageAddDialog>("Add recipe stage", new Dictionary<string, object?>
            {
                [nameof(RecipeStageAddDialog.Templates)] = catalog.Templates,
                [nameof(RecipeStageAddDialog.InitialTagPrefix)] = prefix
            }, new DialogOptions { Width = "620px", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (input is not RecipeStageAddRequest request)
                return;

            request.ExpectedStages = expectedStages;
            requestStarted = true;
            _loading = true;

            var result = await RecipeApi.AddStageAsync(recipeId, request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.SelectedStageId is null)
                throw new InvalidOperationException("Unexpected add stage response. Press Refresh.");

            saved = true;

            var snapshot = await RecipeApi.GetAsync(recipeId, ct);
            ct.ThrowIfCancellationRequested();

            _snapshot = snapshot;
            RestoreStageSelection(result.SelectedStageId);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Stage and its parameters added to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // При уходе со страницы незавершённые запросы отменяются; вставку не повторяем.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Failed to add a stage to recipe {RecipeId}.", recipeId);

                if (requestStarted)
                {
                    _snapshot = null;
                    RestoreStageSelection(null);
                }

                _error = saved ? "The stage was added, but the recipe could not be reloaded. Press Refresh. " + ex.Message
                    : !requestStarted || ex is HttpRequestException { StatusCode: not null } ? ex.Message : "Add stage result could not be confirmed. Press Refresh before retrying. " + ex.Message;
            }
        }
        finally
        {
            _dialogOpen = false;
            _changingStage = false;
            _loading = false;
        }
    }
}