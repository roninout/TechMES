namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Общие вспомогательные операции batch-чтения для CtApi,
/// Mock и Disabled gateway.
/// </summary>
internal static class ScadaTagBatchHelper
{
    /// <summary>
    /// Удаляет пробелы и дубликаты, сохраняя исходный порядок тегов.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTagNames(IReadOnlyCollection<string> tagNames)
    {
        ArgumentNullException.ThrowIfNull(tagNames);

        var result = new List<string>(tagNames.Count);
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in tagNames)
        {
            var normalized = (tagName ?? "").Trim();

            if (normalized.Length > 0 && uniqueNames.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }
}