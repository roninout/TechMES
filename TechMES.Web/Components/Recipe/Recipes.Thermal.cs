using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    /// <summary>В PLC-режиме используем только прочитанные значения, без подмены данными БД.</summary>
    private bool? StageBooleanValue(RecipeStageDto stage, string name)
    {
        var column = Columns.FirstOrDefault(item => item.Kind == RecipeParameterKind.CheckBox && item.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Columns.FirstOrDefault(item => item.Kind == RecipeParameterKind.CheckBox && item.TagSuffix.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        // Отсутствующий в стадии параметр не включён.
        if (column is null || !stage.Parameters.TryGetValue(column.Id, out var parameter))
            return false;

        if (!_plcEnabled)
            return IsValidRecipeValue(parameter.Value, column.Kind) ? parameter.Value == 1 : null;

        var value = FindPlcValue(stage, column);

        return IsReadablePlcValue(value, column.Kind) ? value!.Value == 1 : null;
    }

    /// <summary>Heating имеет приоритет; неизвестное состояние Heating не выдаём за Cooling.</summary>
    private (string Icon, string Title, string CssClass)? StageThermalState(RecipeStageDto stage)
    {
        var heating = StageBooleanValue(stage, "Heating");

        if (heating == true)
            return ("local_fire_department", "Heating", "is-heating");

        if (heating == false && StageBooleanValue(stage, "Cooling") == true)
            return ("ac_unit", "Cooling", "is-cooling");

        return null;
    }
}