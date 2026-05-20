namespace TechMES.Infrastructure.CtApi.Native;

/// <summary>
/// Тонкий интерфейс над реальным CtApi wrapper-ом.
///
/// Зачем нужен:
/// CtApi.dll — внешняя нативная библиотека Plant SCADA.
/// Мы не хотим, чтобы Runtime.Service или WEB знали детали P/Invoke/CtApi.
/// Все реальные вызовы CtApi спрятаны за этим интерфейсом.
/// </summary>
public interface ICtApiNativeClient
{
    /// <summary>
    /// Открыть соединение с CtApi / Plant SCADA.
    /// </summary>
    Task OpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Закрыть соединение.
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>
    /// Прочитать tag.
    /// </summary>
    Task<string?> TagReadAsync(string tagName, CancellationToken ct = default);

    /// <summary>
    /// Записать tag.
    /// </summary>
    Task TagWriteAsync(string tagName, string? value, CancellationToken ct = default);

    /// <summary>
    /// Выполнить Cicode-команду через CtApi.
    ///
    /// Нужен для тех операций, которые в WPF-проекте тоже делались через Cicode:
    /// - EquipGetProperty(..., "Type", 3);
    /// - TagInfo(...);
    /// - EquipRefBrowseOpen/Next/GetField/Close.
    /// </summary>
    Task<string?> CicodeAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Мягкая проверка связи.
    ///
    /// Не бросает ошибку наружу.
    /// Используется health monitor-ом.
    /// </summary>
    Task<bool> TryProbeConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Выполнить ctFind по таблице Plant SCADA.
    ///
    /// Этот метод нужен для получения каталогов:
    /// - оборудования;
    /// - tags;
    /// - trend tags;
    /// - других таблиц Plant SCADA.
    ///
    /// Runtime.Service не должен знать детали ctFindFirst/ctFindNext.
    /// Он получает уже готовые строки как Dictionary.
    /// </summary>
    Task<IReadOnlyList<Dictionary<string, string>>> FindAsync(string tableName, string? filter, string? cluster, IReadOnlyList<string> properties, CancellationToken ct = default);
}
