using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Web.Clients;

/// <summary>
/// Разрешает пользовательскую ссылку ProcessInput
/// в реальный числовой Plant SCADA Variable Tag.
///
/// Поддерживаются два варианта ввода.
///
/// Вариант 1 — Runtime Equipment + ITEM:
///
///     S03.R02.TT01.R
///
/// Resolver ищет наиболее длинное подходящее Equipment:
///
///     Equipment = S03.R02.TT01
///     ITEM      = R
///
/// Затем через существующий Param snapshot получает реальный TagName
/// именно указанного ITEM.
///
/// Никакой ITEM автоматически не подставляется.
/// Resolver ничего не предполагает ни про R, ни про тип Equipment.
///
/// Вариант 2 — прямой Variable Tag:
///
///     S03_R02_TT01_R
///
/// Он проверяется существующим ParamApi.CheckNumericTagAsync().
///
/// В обоих случаях результатом является уже разрешённый TagName,
/// который затем сохраняется в Calc Job и непосредственно читается Calc.Service.
/// </summary>
public sealed class CalcProcessInputResolver(ParamApiClient paramApi)
{
    /// <summary>
    /// Разрешает одну пользовательскую ссылку.
    ///
    /// Сначала пытаемся интерпретировать её как Equipment.ITEM.
    /// Если это не удалось, выполняем direct Variable Tag check.
    ///
    /// Success = true означает, что Runtime реально прочитал
    /// конечный Variable Tag как числовой.
    /// </summary>
    public async Task<CalcProcessInputResolution> ResolveAsync(string? sourceText, IReadOnlyList<EquipmentDto> equipmentCatalog, CancellationToken ct = default)
    {
        var source = (sourceText ?? "").Trim();

        if (source.Length == 0)
            return CalcProcessInputResolution.Failed(source, "Process input source is empty.");

        var equipmentReference = FindEquipmentItemReference(source, equipmentCatalog);
        string? equipmentFailureMessage = null;

        if (equipmentReference is not null)
        {
            try
            {
                var snapshot = await paramApi.GetSnapshotAsync(equipmentReference.Equipment.Name, ct);

                var item = snapshot.Items.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, equipmentReference.ItemName, StringComparison.OrdinalIgnoreCase));

                if (snapshot.Supported
                    && item?.NumericValue is { } numericValue
                    && double.IsFinite(numericValue)
                    && !string.IsNullOrWhiteSpace(item.TagName))
                {
                    return CalcProcessInputResolution.Linked(
                        source,
                        item.TagName.Trim(),
                        numericValue,
                        $"Runtime {equipmentReference.Equipment.Name}.{item.Name}");
                }

                if (!snapshot.Supported)
                {
                    equipmentFailureMessage = snapshot.Message;

                    if (string.IsNullOrWhiteSpace(equipmentFailureMessage))
                        equipmentFailureMessage = $"Param snapshot is not supported for Equipment '{equipmentReference.Equipment.Name}'.";
                }
                else if (item is null)
                {
                    equipmentFailureMessage = $"ITEM '{equipmentReference.ItemName}' was not found in Runtime Param snapshot for '{equipmentReference.Equipment.Name}'.";
                }
                else if (string.IsNullOrWhiteSpace(item.TagName))
                {
                    equipmentFailureMessage = $"ITEM '{equipmentReference.ItemName}' was found, but its Variable Tag was not resolved.";
                }
                else
                {
                    equipmentFailureMessage = $"ITEM '{equipmentReference.ItemName}' was found, but its current value is not numeric.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                equipmentFailureMessage = $"Cannot resolve Runtime reference '{source}': {ex.Message}";
            }
        }

        // Если Equipment.ITEM не разрешился, исходную строку проверяем
        // как уже готовый Plant SCADA Variable Tag.
        //
        // Это тот же общий механизм, которым пользуется Test Kp online tag.
        try
        {
            var direct = await paramApi.CheckNumericTagAsync(new ParamTagCheckRequest
            {
                TagName = source,
                RequireTrend = false
            }, ct);

            if (direct.Found && direct.CurrentValue.HasValue)
            {
                return CalcProcessInputResolution.Linked(
                    source,
                    direct.TagName,
                    direct.CurrentValue.Value,
                    "Direct SCADA tag");
            }

            var message = direct.Message ?? "Numeric SCADA tag was not found.";

            if (!string.IsNullOrWhiteSpace(equipmentFailureMessage))
                message = $"{equipmentFailureMessage} Direct tag check: {message}";

            return CalcProcessInputResolution.Failed(source, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Cannot check SCADA tag '{source}': {ex.Message}";

            if (!string.IsNullOrWhiteSpace(equipmentFailureMessage))
                message = $"{equipmentFailureMessage} {message}";

            return CalcProcessInputResolution.Failed(source, message);
        }
    }

    /// <summary>
    /// Ищет Equipment-часть полной ссылки Equipment.ITEM.
    ///
    /// Мы не разбиваем строку просто по последней точке,
    /// потому что имя Equipment само содержит точки.
    ///
    /// Вместо этого ищем наиболее длинное имя Equipment,
    /// являющееся началом введённой ссылки.
    ///
    /// Например:
    ///
    /// source = S03.R02.TT01.R
    ///
    /// найдено:
    /// Equipment = S03.R02.TT01
    ///
    /// оставшаяся часть:
    /// ITEM = R
    ///
    /// Такой подход не содержит никаких знаний о конкретном ITEM.
    /// </summary>
    private static EquipmentItemReference? FindEquipmentItemReference(string source, IReadOnlyList<EquipmentDto> equipmentCatalog)
    {
        var equipment = equipmentCatalog
            .Where(item => !item.IsGroup && !item.IsEquipmentChildNode)
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Where(item => source.Length > item.Name.Length + 1)
            .Where(item => source.StartsWith(item.Name + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Name.Length)
            .FirstOrDefault();

        if (equipment is null)
            return null;

        var itemName = source[(equipment.Name.Length + 1)..].Trim();

        if (itemName.Length == 0)
            return null;

        return new EquipmentItemReference(equipment, itemName);
    }

    private sealed record EquipmentItemReference(EquipmentDto Equipment, string ItemName);
}

/// <summary>
/// Результат разрешения одной пользовательской ProcessInput-ссылки.
/// </summary>
public sealed record CalcProcessInputResolution(bool Success, string SourceText, string? ResolvedTagName, double? CurrentValue, string? Resolution, string? Message)
{
    public static CalcProcessInputResolution Linked(string sourceText, string tagName, double currentValue, string resolution)
    {
        return new CalcProcessInputResolution(
            Success: true,
            SourceText: sourceText,
            ResolvedTagName: tagName,
            CurrentValue: currentValue,
            Resolution: resolution,
            Message: null);
    }

    public static CalcProcessInputResolution Failed(string sourceText, string? message)
    {
        return new CalcProcessInputResolution(
            Success: false,
            SourceText: sourceText,
            ResolvedTagName: null,
            CurrentValue: null,
            Resolution: null,
            Message: message);
    }
}