namespace TechMES.Web.State;

public sealed class EquipmentFooterState
{
    public event Action? Changed;

    public int NodeCount { get; private set; }

    public int MatchCount { get; private set; }

    public void SetCounts(int nodeCount, int matchCount)
    {
        if (NodeCount == nodeCount && MatchCount == matchCount)
            return;

        NodeCount = nodeCount;
        MatchCount = matchCount;

        Changed?.Invoke();
    }

    public void Clear()
    {
        SetCounts(0, 0);
    }
}
