namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка состояния диска для вкладки сервисной очистки.
/// </summary>
public sealed class DiskStatusViewModel
{
    /// <summary>
    /// Название проверяемой области: Publish root, Backup root и т.д.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Путь, по которому определялся диск.
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// Имя диска, например C:\.
    /// </summary>
    public string Drive { get; init; } = "";

    /// <summary>
    /// Свободное место.
    /// </summary>
    public string FreeText { get; init; } = "";

    /// <summary>
    /// Общий размер диска.
    /// </summary>
    public string TotalText { get; init; } = "";

    /// <summary>
    /// Итоговый статус: OK, Warning или Error.
    /// </summary>
    public string Status { get; init; } = "";
}
