namespace TechMES.Maintenance.Models;

/// <summary>
/// Результат запуска внешней команды.
/// Используется для sc.exe, чтобы не тянуть дополнительных NuGet-зависимостей для управления службами.
/// </summary>
public sealed class ProcessResult
{
    /// <summary>
    /// Код завершения процесса.
    /// Ноль обычно означает успех, но конкретный смысл зависит от команды.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Стандартный вывод процесса.
    /// </summary>
    public string StandardOutput { get; init; } = "";

    /// <summary>
    /// Стандартный поток ошибок процесса.
    /// </summary>
    public string StandardError { get; init; } = "";

    /// <summary>
    /// true, если процесс был остановлен по таймауту.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Объединенный текст stdout/stderr для отображения в диагностическом окне.
    /// </summary>
    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
