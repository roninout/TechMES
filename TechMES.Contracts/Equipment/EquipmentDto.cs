namespace TechMES.Contracts.Equipment;

/// <summary>
/// Описание единицы оборудования для WEB-интерфейса.
/// 
/// Это не CtApi-модель напрямую.
/// Это contract между Runtime.Service и WEB.
/// </summary>
public sealed class EquipmentDto
{
    private string _displayName = "";
    private string _description = "";
    private string _location = "";

    /// <summary>
    /// Полное имя оборудования.
    /// Пример: S01.H01.P01
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Текст для отображения в WEB.
    /// Обычно может совпадать с Name или быть COMMENT/DESCRIPTION из CtApi.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set => _displayName = CleanScadaText(value);
    }

    /// <summary>
    /// Описание оборудования.
    /// </summary>
    public string Description
    {
        get => _description;
        set => _description = CleanScadaText(value);
    }

    /// <summary>
    /// SCADA location/channel metadata.
    /// В WPF-проекте это поле читалось через EquipGetProperty(equipment, "Custom1", 3).
    /// </summary>
    public string Location
    {
        get => _location;
        set => _location = CleanScadaText(value);
    }

    /// <summary>
    /// Станция.
    /// Пример: S01
    /// </summary>
    public string Station { get; set; } = "";

    /// <summary>
    /// Текстовое имя типа.
    /// Пример: Motor, AI, VGA.
    /// </summary>
    public string TypeName { get; set; } = "";

    /// <summary>
    /// Группа типа оборудования.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Признак group node.
    /// В WPF у тебя уже есть группы Equipment.
    /// В WEB мы сразу оставляем это поле на будущее.
    /// </summary>
    public bool IsGroup { get; set; }

    /// <summary>
    /// Родительская группа.
    /// Пока может быть пусто.
    /// Позже пригодится для tree/list структуры.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// Признак избранного.
    /// Пока mock, позже будет из БД per-device.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Уникальный идентификатор узла для будущего Tree UI.
    ///
    /// Важно: одно и то же физическое оборудование может быть показано дважды:
    /// - как обычный root-node;
    /// - как child-node внутри Equipment-группы.
    /// Поэтому для дерева нужен отдельный NodeId, а не только Name.
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// Родительский узел для будущего Tree UI.
    /// Для root-node используем "0".
    /// </summary>
    public string ParentNodeId { get; set; } = "0";

    /// <summary>
    /// Признак child-node внутри Equipment-группы.
    /// Физически это то же оборудование, но в дереве оно находится под group node.
    /// </summary>
    public bool IsEquipmentChildNode { get; set; }

    /// <summary>
    /// Количество фото в Info-модуле.
    /// </summary>
    public int PhotoCount { get; set; }

    /// <summary>
    /// Количество PDF-инструкций в Info-модуле.
    /// </summary>
    public int InstructionCount { get; set; }

    /// <summary>
    /// Количество схем в Info-модуле.
    /// </summary>
    public int SchemeCount { get; set; }

    /// <summary>
    /// Количество заметок в Info-модуле.
    /// </summary>
    public int NoteCount { get; set; }

    /// <summary>
    /// Очищает SCADA-localized текст вида @(...) перед отображением в WEB.
    /// </summary>
    private static string CleanScadaText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Trim();

        if (text.StartsWith("@(", StringComparison.Ordinal)
            && text.EndsWith(")", StringComparison.Ordinal)
            && text.Length >= 3)
        {
            text = text[2..^1].Trim();

            if (text.Length >= 2
                && ((text[0] == '"' && text[^1] == '"')
                    || (text[0] == '\'' && text[^1] == '\'')))
            {
                text = text[1..^1].Trim();
            }
        }

        return text;
    }
}
