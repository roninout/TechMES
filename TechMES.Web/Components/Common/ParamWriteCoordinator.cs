using Microsoft.Extensions.Options;
using Radzen;
using TechMES.Contracts.Param;
using TechMES.Web.Clients;
using TechMES.Web.Settings;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Общая WEB-точка входа в существующий Param write-flow.
/// Использует тот же ParamWriteDialog, ParamApiClient, confirm, Windows actor enrichment и Runtime endpoint, что Equipment -> Param.
/// </summary>
public sealed class ParamWriteCoordinator(ParamApiClient paramApi, IOptions<ParamUiOptions> options, DialogService dialogService, NotificationService notificationService)
{
    private bool _isBusy;

    /// <summary>
    /// Открывает общий Param write-dialog и выполняет запись только после подтверждения пользователя.
    /// Null означает, что пользователь закрыл dialog/confirm либо другая запись уже выполняется.
    /// </summary>
    public async Task<ParamWriteResponse?> WriteAsync(string equipmentName, ParamItemDto item, CancellationToken ct = default)
    {
        if (_isBusy || string.IsNullOrWhiteSpace(equipmentName) || item is null || !item.CanWrite)
            return null;

        _isBusy = true;

        try
        {
            var dialogResult = await dialogService.OpenAsync<ParamWriteDialog>($"Write: {item.Name}", new Dictionary<string, object?>
            {
                [nameof(ParamWriteDialog.Item)] = item
            }, new DialogOptions
            {
                Width = "420px",
                CloseDialogOnOverlayClick = false,
                CloseDialogOnEsc = true
            });

            if (dialogResult is not ParamWriteDialogResult writeResult)
                return null;

            if (options.Value.ConfirmWrites && !await ConfirmAsync(equipmentName, item, writeResult.Value))
                return null;

            var response = await paramApi.WriteAsync(equipmentName, new ParamWriteRequest
            {
                ItemName = item.Name,
                Value = writeResult.Value,
                Comment = item.Name
            }, ct);

            Notify(response, item);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            notificationService.Notify(NotificationSeverity.Error, "Param write", "Cannot write Param value: " + ex.Message, 4500);
            return new ParamWriteResponse { EquipmentName = equipmentName, ItemName = item.Name, Success = false, Error = ex.Message };
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task<bool> ConfirmAsync(string equipmentName, ParamItemDto item, string value)
    {
        var current = FormatValue(item);
        var enablesForce = item.Name.Equals("ForceCmd", StringComparison.OrdinalIgnoreCase) && item.Kind == ParamValueKind.Boolean && value == "1";
        var title = enablesForce ? "Attention!!!" : "Confirm Param write";
        var message = enablesForce
            ? $"Do you really want to enable channel forcing?\n\nEquipment: {equipmentName}\nItem: {item.Name}\nCurrent: {current}\nNew: on"
            : $"Write Param value?\n\nEquipment: {equipmentName}\nItem: {item.Name}\nCurrent: {current}\nNew: {DisplayValue(value)}";
        var confirmed = await dialogService.Confirm(message, title, new ConfirmOptions { OkButtonText = "Write", CancelButtonText = "Cancel" });
        return confirmed == true;
    }

    private void Notify(ParamWriteResponse response, ParamItemDto item)
    {
        if (!response.Success)
        {
            notificationService.Notify(NotificationSeverity.Warning, "Param write", response.Error ?? response.Message ?? "Param write was rejected.", 4500);
            return;
        }

        var auditWarning = !response.DryRun && response.AuditAttempted && !response.AuditSucceeded;
        var severity = response.DryRun ? NotificationSeverity.Info : auditWarning ? NotificationSeverity.Warning : NotificationSeverity.Success;
        notificationService.Notify(severity, "Param write", response.Message ?? BuildWriteStatus(response, item), 3500);
    }

    private static string BuildWriteStatus(ParamWriteResponse response, ParamItemDto item)
    {
        var mode = response.DryRun ? "Dry-run OK" : "Wrote";
        var current = DisplayValue(response.CurrentValue);
        var written = DisplayValue(response.WrittenValue);
        var parts = new List<string> { $"{mode}: {response.EquipmentName}.{item.Name} {current} -> {written}" };

        if (!string.IsNullOrWhiteSpace(response.TagName))
            parts.Add($"Tag: {response.TagName}");

        if (response.AuditAttempted)
            parts.Add(response.AuditSucceeded ? "Audit: OK" : "Audit: failed");

        return string.Join(" | ", parts);
    }

    private static string FormatValue(ParamItemDto item)
    {
        if (item.Kind == ParamValueKind.Boolean && item.BooleanValue.HasValue)
            return item.BooleanValue.Value ? "on" : "off";

        return DisplayValue(item.ValueText);
    }

    private static string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}