using System.Net;
using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

public partial class Recipes
{
    private bool CanDeleteRecipe => CanManageRecipe && !IsCurrentRecipe(_snapshot?.Recipe.Id) && !IsAssignedBaseRecipe(_snapshot?.Recipe.Id) && _savedSnapshot is not null;

    /// <summary>Подтверждает удаление; Runtime повторно проверяет Current и исходный снимок.</summary>
    private async Task DeleteRecipeAsync()
    {
        if (!CanDeleteRecipe || _savedSnapshot is null) return;

        var original = CloneRecipe(_savedSnapshot);
        var message = $"Delete recipe {original.Recipe.DisplayName} and all its stages?";

        if (HasRecipeChanges)
            message += " Unsaved changes in this draft will also be discarded.";

        _dialogOpen = true;
        bool accepted;

        try
        {
            accepted = await DialogService.Confirm(message, "Delete recipe", new ConfirmOptions { OkButtonText = "Delete", CancelButtonText = "Cancel" }) == true;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (!accepted || _disposed) return;

        var ct = _stop.Token;
        var deleted = false;

        _managingRecipe = true;
        _error = null;

        try
        {
            var result = await RecipeApi.ChangeAsync(new RecipeChangeRequest
            {
                Operation = RecipeChangeOperation.Delete,
                SourceRecipeId = original.Recipe.Id,
                ExpectedSnapshot = original
            }, ct);

            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved)
                throw new InvalidOperationException("Unexpected recipe delete response.");

            deleted = true;
            _snapshot = _savedSnapshot = null;
            _hasRecipeChanges = false;
            _selectedRecipeId = null;
            _loading = true;

            ClearStageDrag();

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(null, ct);

            _selectorRevision++;

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Recipe deleted.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Failed to delete recipe {RecipeId}.", original.Recipe.Id);

                _uncertainDraft = ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = deleted ? "Recipe deleted, but the page could not be refreshed. Press Refresh. " + ex.Message
                    : _uncertainDraft ? "Delete result is uncertain. Press Refresh before continuing. " + ex.Message : ex.Message;
            }
        }
        finally
        {
            _managingRecipe = false;
            _loading = false;
        }
    }
}