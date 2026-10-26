using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

/// <summary>Управление заголовками рецептов и загрузкой выбранного семейства.</summary>
public partial class Recipes
{
    private bool _managingRecipe;

    private int? EditorFamilyId => SelectedPage?.BaseRecipeId ?? (_snapshot is null ? RecipeEditorNavigation.ReadFamily(Navigation.Uri) : _catalog.Pages.Any(page => page.BaseRecipeId == _snapshot.Recipe.Id) ? _snapshot.Recipe.Id : _snapshot.Recipe.BaseRecipeId);
    private bool CanManageRecipe => _canEdit && !IsBusy && !_dialogOpen && !_uncertainDraft && _snapshot is not null;
    private int? NewRecipeSourceId => _snapshot?.Recipe.Id ?? SelectedPage?.BaseRecipeId;

    /// <summary>Восстанавливает Unit и загружает Current при первом открытии просмотра.</summary>
    private async Task ReloadCatalogAndRecipeAsync(int? preferredRecipeId, CancellationToken ct)
    {
        var catalog = await RecipeApi.GetCatalogAsync(ct);
        ct.ThrowIfCancellationRequested();

        var firstLoad = !_catalogLoaded;
        _catalog = catalog;

        if (firstLoad)
        {
            RestoreRecipeScope();
        }
        else if (_selectedPageId.HasValue && !_catalog.Pages.Any(page => page.Id == _selectedPageId))
        {
            _selectedPageId = _catalog.Pages.FirstOrDefault()?.Id;
        }

        // В просмотре всегда нужен конкретный Unit; старую ссылку unit=all восстанавливаем на первый.
        if (firstLoad && !EditorMode && SelectedPage is null)
            _selectedPageId = _catalog.Pages.FirstOrDefault()?.Id;

        _catalogLoaded = true;

        // При первом открытии просмотра выбираем Current. Refresh сохраняет ручной выбор.
        var recipeId = firstLoad && !EditorMode ? SelectedPage?.RecipeId : preferredRecipeId ?? (firstLoad ? ReadQueryId("recipe") : null);
        SelectAvailableRecipes(recipeId);

        await LoadSelectedRecipeAsync(ct);
    }

    /// <summary>Предлагает свободное имя; окончательная проверка выполняется сервером.</summary>
    private string SuggestRecipeName(RecipeChangeOperation operation)
    {
        if (operation == RecipeChangeOperation.Rename)
            return _snapshot?.Recipe.Name ?? "";

        var basis = operation == RecipeChangeOperation.Copy ? $"{_snapshot?.Recipe.Name} - copy" : "New recipe";
        var pageName = CreationPage?.Page;

        if (!string.IsNullOrWhiteSpace(pageName) && !basis.Contains(pageName, StringComparison.OrdinalIgnoreCase))
            basis = $"{pageName} - {basis}";

        var suffix = 1;

        while (_catalog.Recipes.Any(recipe => recipe.Name.Equals($"{basis} {suffix}", StringComparison.OrdinalIgnoreCase)))
            suffix++;

        return $"{basis} {suffix}";
    }

    /// <summary>Rename сразу сохраняет имя в БД; New/Copy сохраняют прежнее поведение.</summary>
    private async Task ChangeRecipeAsync(RecipeChangeOperation operation)
    {
        var sourceId = operation == RecipeChangeOperation.Create ? NewRecipeSourceId : _snapshot?.Recipe.Id;

        if (IsBusy || _disposed || !_canEdit || _dialogOpen || _uncertainDraft || sourceId is null)
            return;

        // Переименование не сохраняет и не сбрасывает изменения стадий и значений.
        if (operation != RecipeChangeOperation.Rename && !await SaveBeforeManagementAsync())
            return;

        // Для Rename передаём последнее подтверждённое имя из БД.
        var expectedName = operation == RecipeChangeOperation.Rename ? _savedSnapshot?.Recipe.Name ?? _snapshot?.Recipe.Name : _snapshot?.Recipe.Name;
        var ct = _stop.Token;
        int? savedRecipeId = null;
        var requestSent = false;

        _managingRecipe = true;
        _error = null;

        try
        {
            var title = operation switch
            {
                RecipeChangeOperation.Create => "New recipe",
                RecipeChangeOperation.Copy => "Copy recipe",
                _ => "Rename recipe"
            };

            var description = operation switch
            {
                RecipeChangeOperation.Create => "Creates an empty working recipe in the same recipe family.",
                RecipeChangeOperation.Copy => "Creates a working copy in the same family, including all stages and parameter values.",
                _ => "Saves the new recipe name to the database immediately. Unsaved stage and parameter changes remain in the editor."
            };

            _dialogOpen = true;

            var input = await DialogService.OpenAsync<RecipeNameDialog>(title, new Dictionary<string, object?>
            {
                [nameof(RecipeNameDialog.InitialName)] = SuggestRecipeName(operation),
                [nameof(RecipeNameDialog.Description)] = description,
                [nameof(RecipeNameDialog.RequiredNamePart)] = operation == RecipeChangeOperation.Rename ? SelectedPage?.Page : CreationPage?.Page
            }, new DialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (input is not string name)
                return;

            // Все три операции выполняются сервером сразу после подтверждения окна.
            requestSent = true;

            var result = await RecipeApi.ChangeAsync(new RecipeChangeRequest
            {
                Operation = operation,
                SourceRecipeId = sourceId.Value,
                Name = name,
                ExpectedName = expectedName
            }, ct);

            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.RecipeId is not int recipeId)
                throw new InvalidOperationException("Unexpected recipe operation response. Refresh the list.");

            if (operation == RecipeChangeOperation.Rename && recipeId != sourceId.Value)
                throw new InvalidOperationException("Rename response does not match the selected recipe.");

            savedRecipeId = recipeId;

            if (operation == RecipeChangeOperation.Rename)
            {
                // Меняем только имя. Стадии, значения и выделение строки сохраняем.
                _snapshot!.Recipe.Name = name;

                // Обновляем базовый снимок, чтобы следующий Save не считал Rename конфликтом.
                if (_savedSnapshot is not null)
                {
                    _savedSnapshot.Recipe.Name = name;
                    _savedRecipeStamp = JsonSerializer.Serialize(_savedSnapshot);
                }

                // Обновляем название в каталоге и текущем списке комбобокса.
                foreach (var recipe in _catalog.Recipes.Where(item => item.Id == recipeId))
                    recipe.Name = name;

                foreach (var recipe in _availableRecipes.Where(item => item.Id == recipeId))
                    recipe.Name = name;

                _selectorRevision++;

                // Остальные несохранённые изменения продолжают учитываться кнопкой Save.
                MarkRecipeChanges();

                NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Recipe renamed in the database.", 2500);
                return;
            }

            // New/Copy создают рабочий рецепт, а не новое семейство.
            if (IsBaseRecipeMode)
            {
                _selectedPageId = CreationPage?.Id;
                _recipeScope = "recipes";
                _selectorRevision++;
            }

            _selectedRecipeId = recipeId;
            _loading = true;
            _snapshot = null;

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(recipeId, ct);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Recipe saved to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // После закрытия страницы команду автоматически не повторяем.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Recipe operation failed. Operation={Operation}, Source={SourceId}.", operation, sourceId);

                // При потере ответа Rename мог уже выполниться.
                // Не разрешаем последующий Save со старым базовым именем до Refresh.
                var uncertainRename = operation == RecipeChangeOperation.Rename && requestSent && !savedRecipeId.HasValue
                    && ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };

                if (uncertainRename)
                    _uncertainDraft = true;

                _error = savedRecipeId.HasValue
                    ? $"Recipe [{savedRecipeId}] was saved, but the interface could not be updated. Press Refresh. {ex.Message}"
                    : uncertainRename
                        ? "Rename result could not be confirmed. Press Refresh before saving further changes. " + ex.Message
                        : ex is HttpRequestException { StatusCode: not null }
                            ? ex.Message
                            : "Operation result could not be confirmed. Refresh the list before retrying. " + ex.Message;
            }
        }
        finally
        {
            _dialogOpen = false;
            _managingRecipe = false;
            _loading = false;
        }
    }

}