namespace TechMES.Contracts.Param;

/// <summary>Диапазон истории произвольного Variable Tag. Время передаётся в UTC.</summary>
public sealed class ParamTagTrendRequest
{
    public string TagName { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
}