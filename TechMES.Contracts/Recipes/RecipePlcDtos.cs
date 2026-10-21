using System.Security.Cryptography;
using System.Text.Json;

namespace TechMES.Contracts.Recipes;

/// <summary>Отдельный снимок PLC: сохранённые значения рецепта в БД не изменяются.</summary>
public sealed class RecipePlcSnapshotDto
{
    public int RecipeId { get; set; }
    public int PageId { get; set; }
    public string LayoutStamp { get; set; } = "";
    public DateTimeOffset ReadAtUtc { get; set; }
    public List<RecipePlcValueDto> Values { get; set; } = [];
}

/// <summary>Ошибка чтения остаётся ошибкой, а не преобразуется в числовой ноль.</summary>
public sealed class RecipePlcValueDto
{
    public int StageId { get; set; }
    public int ColumnId { get; set; }
    public string TagName { get; set; } = "";
    public double? Value { get; set; }
    public string? Error { get; set; }
}

/// <summary>Проверяет, что WEB и Runtime используют одинаковую адресацию стадий и параметров.</summary>
public static class RecipePlcLayout
{
    public static string GetStamp(RecipeSnapshotDto snapshot)
    {
        // Значения и названия не участвуют: контролируем именно структуру и адреса тегов.
        var layout = new
        {
            RecipeId = snapshot.Recipe.Id,
            Columns = snapshot.Columns.OrderBy(column => column.Id).Select(column => new { column.Id, column.TagSuffix, column.Kind }),
            Stages = snapshot.Stages.OrderBy(stage => stage.Id).Select(stage => new { stage.Id, stage.Number, stage.TagPrefix, Parameters = stage.Parameters.Keys.OrderBy(id => id).ToArray() })
        };

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(layout)));
    }
}