namespace TechMES.Web.State;

public sealed class EquipmentFooterState
{
    public event Action? Changed;

    public int NodeCount { get; private set; }

    public int PhotoCount { get; private set; }

    public int PdfCount { get; private set; }

    public int SchemeCount { get; private set; }

    public int NoteCount { get; private set; }

    public void SetTreeCount(int nodeCount)
    {
        if (NodeCount == nodeCount)
            return;

        NodeCount = nodeCount;

        Changed?.Invoke();
    }

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

    public void ClearInfoCounts()
    {
        SetInfoCounts(0, 0, 0, 0);
    }

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
