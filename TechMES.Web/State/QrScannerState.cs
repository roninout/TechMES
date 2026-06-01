namespace TechMES.Web.State;

/// <summary>
/// Scoped-мост между глобальным QR scanner в MainLayout и страницами,
/// которые умеют интерпретировать QR-текст.
/// В Blazor Server это состояние принадлежит одному browser circuit,
/// поэтому каждая вкладка браузера получает собственный поток QR-событий.
/// </summary>
public sealed class QrScannerState
{
    /// <summary>
    /// Событие успешного сканирования QR.
    /// Подписчик сам решает, как трактовать текст: выбрать оборудование, показать ошибку и т.д.
    /// </summary>
    public event Func<string, Task>? Scanned;

    /// <summary>
    /// Последний считанный QR-текст. Полезно для диагностики и повторной обработки.
    /// </summary>
    public string? LastScannedText { get; private set; }

    /// <summary>
    /// Сохраняет последнее значение и уведомляет активные страницы.
    /// Сам scanner не выбирает оборудование; правило выбора живет в Equipment.razor.
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
