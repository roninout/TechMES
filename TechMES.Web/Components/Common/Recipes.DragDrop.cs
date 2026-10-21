using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>Перетаскивание работает только с актуальным снимком выбранного рецепта.</summary>
public partial class Recipes
{
    private RecipeStageTemplateCatalogDto _stageTemplates = new();
    private string _templateSearch = "";
    private bool _showStageTags;
    private RecipeSnapshotDto? _dragSnapshot;
    private int? _draggedStageId, _draggedTemplateId, _dropTargetId;
    private bool _dropAtEnd;

    private IEnumerable<RecipeStageTemplateDto> FilteredStageTemplates => _stageTemplates.Templates.Where(item => item.Name.Contains(_templateSearch.Trim(), StringComparison.OrdinalIgnoreCase) || item.Code.Contains(_templateSearch.Trim(), StringComparison.OrdinalIgnoreCase));
    private bool HasCurrentStageDrag => CanAddStage && _dragSnapshot is not null && ReferenceEquals(_dragSnapshot, _snapshot) && (_draggedStageId.HasValue || _draggedTemplateId.HasValue);

    /// <summary>Запоминает шаблон из каталога выбранного семейства.</summary>
    private void BeginTemplateDrag(int templateId)
    {
        ClearStageDrag();

        if (!CanAddStage || !_stageTemplates.Templates.Any(item => item.Id == templateId)) return;

        _dragSnapshot = _snapshot;
        _draggedTemplateId = templateId;
    }

    /// <summary>Запоминает ID строки, который сохраняется при перенумерации.</summary>
    private void BeginStageDrag(int stageId)
    {
        ClearStageDrag();

        if (!CanAddStage || !Stages.Any(item => item.Id == stageId)) return;

        _dragSnapshot = _snapshot;
        _draggedStageId = stageId;
    }

    /// <summary>Очищает перенос при завершении, отмене или смене рецепта.</summary>
    private void ClearStageDrag()
    {
        _dragSnapshot = null;
        _draggedStageId = _draggedTemplateId = _dropTargetId = null;
        _dropAtEnd = false;
    }

    /// <summary>Подсвечивает строку назначения без изменения данных.</summary>
    private void MarkStageDrop(int stageId)
    {
        if (!HasCurrentStageDrag) return;

        _dropTargetId = stageId;
        _dropAtEnd = false;
    }

    /// <summary>Нижняя область означает добавление или перенос в конец.</summary>
    private void MarkStageDropEnd(DragEventArgs args)
    {
        if (!HasCurrentStageDrag) return;

        _dropTargetId = null;
        _dropAtEnd = true;
    }

    /// <summary>Подключает HTML Drag & Drop через RowRender Radzen.</summary>
    private void RenderStageRow(RowRenderEventArgs<RecipeStageDto> args)
    {
        if (args.Data is not { } stage) return;

        // RowRender заменяет class: сохраняем базовый класс и выделение Radzen.
        var classes = new List<string> { "rz-data-row" };
        if (_selectedStages.Any(item => item.Id == stage.Id)) classes.Add("rz-state-highlight");

        if (EditorMode && string.IsNullOrWhiteSpace(stage.TagPrefix)) classes.Add("recipes-missing-prefix");
        if (HasCurrentStageDrag && _dropTargetId == stage.Id) classes.Add("recipes-drop-target");

        args.Attributes["class"] = string.Join(" ", classes);
        args.Attributes["draggable"] = CanAddStage ? "true" : "false";

        if (!CanAddStage) return;

        args.Attributes["style"] = "cursor: grab;";
        args.Attributes["ondragover"] = "event.preventDefault();";
        args.Attributes["ondragstart"] = EventCallback.Factory.Create<DragEventArgs>(this, () => BeginStageDrag(stage.Id));
        args.Attributes["ondragenter"] = EventCallback.Factory.Create<DragEventArgs>(this, () => MarkStageDrop(stage.Id));
        args.Attributes["ondragend"] = EventCallback.Factory.Create<DragEventArgs>(this, ClearStageDrag);
        args.Attributes["ondrop"] = EventCallback.Factory.Create<DragEventArgs>(this, () => DropStageAsync(stage.Id));
    }

    /// <summary>Принимает перенос в свободную область таблицы, включая пустой рецепт.</summary>
    private Task DropStageAtEndAsync(DragEventArgs args) => DropStageAsync(null);

    /// <summary>Поглощает перенос до первого await, исключая повторную обработку всплывшего drop.</summary>
    private async Task DropStageAsync(int? targetId)
    {
        if (!HasCurrentStageDrag) { ClearStageDrag(); return; }

        var stageId = _draggedStageId;
        var templateId = _draggedTemplateId;
        var targetIndex = targetId.HasValue ? _snapshot!.Stages.FindIndex(item => item.Id == targetId) : Stages.Count - 1;

        ClearStageDrag();

        if (targetId.HasValue && targetIndex < 0) return;

        if (templateId is int template)
        {
            await AddStageAsync(template, targetId);
            return;
        }

        var sourceIndex = _snapshot!.Stages.FindIndex(item => item.Id == stageId);

        if (sourceIndex < 0 || sourceIndex == targetIndex) return;

        await ChangeStageAsync(RecipeStageChangeOperation.MoveTo, _snapshot.Stages[sourceIndex], targetIndex);
    }
}