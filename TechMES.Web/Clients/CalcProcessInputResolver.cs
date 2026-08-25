using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Web.Clients;

/// <summary>
/// Разрешает пользовательскую ссылку на процессный параметр расчёта в реальный числовой Plant SCADA Variable Tag.
/// Используются только уже существующие Runtime-механизмы:
///
/// 1. Если пользователь ввёл имя Runtime AI Equipment, через Param snapshot берётся ITEM R.
/// 2. Если такое AI Equipment не найдено, введённая строка проверяется как обычный Variable Tag через общий ParamApi.CheckNumericTagAsync().
///
/// Этот класс не содержит CtApi-кода и ничего самостоятельно не знает о структуре Plant SCADA.
/// Он нужен как общая WEB-логика для Density, Capacity и будущих Calculation panels.
/// </summary>
public sealed class CalcProcessInputResolver(ParamApiClient paramApi)
{
    /// <summary>
    /// Разрешает одну пользовательскую ссылку в реальный Variable Tag.
    ///
    /// LINKED можно показывать только если Success = true.
    /// Одного непустого SourceText недостаточно.
    /// </summary>
    public async Task<CalcProcessInputResolution> ResolveAsync(string? sourceText, IReadOnlyList<EquipmentDto> equipmentCatalog, CancellationToken ct = default)
    {
        var source = (sourceText ?? "").Trim();

        if (source.Length == 0)
            return CalcProcessInputResolution.Failed(source, "Process input source is empty.");

        // Сначала ищем именно Runtime AI Equipment.
        // Например пользователь вводит: S03.R02.TT01
        // Если такое AI существует, никакое имя Variable Tag вручную не формируем. Runtime Param snapshot уже умеет разрешать ITEM R в настоящий Plant SCADA TagName.
        var aiEquipment = equipmentCatalog.FirstOrDefault(item =>
            item.TypeGroup == EquipmentTypeGroup.AI
            && !item.IsGroup
            && !item.IsEquipmentChildNode
            && string.Equals(item.Name, source, StringComparison.OrdinalIgnoreCase));

        string? aiFailureMessage = null;

        if (aiEquipment is not null)
        {
            try
            {
                var snapshot = await paramApi.GetSnapshotAsync(aiEquipment.Name, ct);
                var itemR = snapshot.Items.FirstOrDefault(item => string.Equals(item.Name, "R", StringComparison.OrdinalIgnoreCase));

                if (snapshot.Supported
                    && itemR?.NumericValue is { } numericValue
                    && double.IsFinite(numericValue)
                    && !string.IsNullOrWhiteSpace(itemR.TagName))
                {
                    return CalcProcessInputResolution.Linked(
                        source,
                        itemR.TagName.Trim(),
                        numericValue,
                        $"Runtime AI {aiEquipment.Name}.R");
                }

                aiFailureMessage = snapshot.Message;

                if (string.IsNullOrWhiteSpace(aiFailureMessage))
                    aiFailureMessage = $"Runtime AI '{aiEquipment.Name}' was found, but ITEM R was not resolved as a numeric value.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                aiFailureMessage = $"Cannot read Runtime AI '{aiEquipment.Name}': {ex.Message}";
            }
        }

        // Если Runtime AI не разрешился, проверяем исходную строку
        // как обычный Plant SCADA Variable Tag.
        //
        // Это тот же общий механизм, которым после рефакторинга
        // пользуется Test Kp online tag.
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

            var message = direct.Message;

            if (!string.IsNullOrWhiteSpace(aiFailureMessage))
                message = $"{aiFailureMessage} Direct tag check: {message}";

            return CalcProcessInputResolution.Failed(source, message ?? "Numeric SCADA tag was not found.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Cannot check SCADA tag '{source}': {ex.Message}";

            if (!string.IsNullOrWhiteSpace(aiFailureMessage))
                message = $"{aiFailureMessage} {message}";

            return CalcProcessInputResolution.Failed(source, message);
        }
    }
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