namespace TechMES.Web.State;

/// <summary>
/// Scoped-состояние footer-счетчиков для текущей Blazor-сессии.
/// Equipment/Info страницы обновляют это состояние, а EquipmentFooterCountsBlock
/// подписывается на Changed и перерисовывает нижнюю панель.
/// </summary>
public sealed class EquipmentFooterState
{
    /// <summary>
    /// Событие изменения любых footer-счетчиков.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Количество видимых equipment nodes после текущих фильтров.
    /// </summary>
    public int NodeCount { get; private set; }

    /// <summary>
    /// Количество фото у выбранного оборудования.
    /// </summary>
    public int PhotoCount { get; private set; }

    /// <summary>
    /// Количество PDF/instruction файлов у выбранного оборудования.
    /// </summary>
    public int PdfCount { get; private set; }

    /// <summary>
    /// Количество схем у выбранного оборудования.
    /// </summary>
    public int SchemeCount { get; private set; }

    /// <summary>
    /// Количество notes у выбранного оборудования.
    /// </summary>
    public int NoteCount { get; private set; }

    /// <summary>
    /// Обновляет счетчик equipment tree/list. Не вызывает Changed, если значение не изменилось.
    /// </summary>
    public void SetTreeCount(int nodeCount)
    {
        if (NodeCount == nodeCount)
            return;

        NodeCount = nodeCount;

        Changed?.Invoke();
    }

    /// <summary>
    /// Обновляет счетчики Info-модуля для выбранного оборудования.
    /// </summary>
    public void SetInfoCounts(int photoCount, int pdfCount, int schemeCount, int noteCount)
    {
        if (PhotoCount == photoCount
            && PdfCount == pdfCount
            && SchemeCount == schemeCount
            && NoteCount == noteCount)
        {
            return;
        }

        PhotoCount = photoCount;
        PdfCount = pdfCount;
        SchemeCount = schemeCount;
        NoteCount = noteCount;

        Changed?.Invoke();
    }

    /// <summary>
    /// Сбрасывает только Info-счетчики, сохраняя количество nodes.
    /// </summary>
    public void ClearInfoCounts()
    {
        SetInfoCounts(0, 0, 0, 0);
    }

    /// <summary>
    /// Полностью очищает footer-состояние.
    /// </summary>
    public void Clear()
    {
        var changed = NodeCount != 0
            || PhotoCount != 0
            || PdfCount != 0
            || SchemeCount != 0
            || NoteCount != 0;

        NodeCount = 0;
        PhotoCount = 0;
        PdfCount = 0;
        SchemeCount = 0;
        NoteCount = 0;

        if (changed)
            Changed?.Invoke();
    }
}
