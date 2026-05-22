namespace TechMES.Web.State;

/// <summary>
/// Scoped bridge between the global QR scanner in MainLayout and pages that know
/// how to interpret scanned QR text. In Blazor Server this state belongs to one
/// browser circuit, so each opened browser tab gets its own scanner event stream.
/// </summary>
public sealed class QrScannerState
{
    public event Func<string, Task>? Scanned;

    public string? LastScannedText { get; private set; }

    /// <summary>
    /// Stores the latest scanned value and notifies active page components.
    /// The scanner itself does not select equipment; Equipment.razor owns that rule.
    /// </summary>
    public async Task NotifyScannedAsync(string qrText)
    {
        qrText = (qrText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(qrText))
            return;

        LastScannedText = qrText;

        var handlers = Scanned;
        if (handlers is null)
            return;

        foreach (Func<string, Task> handler in handlers.GetInvocationList())
        {
            await handler(qrText);
        }
    }
}
