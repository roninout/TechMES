using TechMES.Contracts.Calc;

namespace TechMES.Application.Calc;

/// <summary>
/// Абстракция хранилища расчётных заданий.
///
/// Runtime.Service работает только с этим интерфейсом и не знает,
/// что конфигурация физически хранится в PostgreSQL.
/// </summary>
public interface ICalcJobStore
{
    /// <summary>
    /// Возвращает все задания вместе с входами и выходами.
    /// </summary>
    Task<IReadOnlyList<CalcJobDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Возвращает одно задание или null, если оно отсутствует.
    /// </summary>
    Task<CalcJobDto?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Создаёт новое задание.
    /// </summary>
    Task<CalcJobDto> CreateAsync(CalcJobSaveRequest request, string? changedBy, CancellationToken ct = default);

    /// <summary>
    /// Обновляет существующее задание.
    /// Возвращает null, если задание отсутствует.
    /// Конфликт Revision будет оформлен отдельным исключением хранилища.
    /// </summary>
    Task<CalcJobDto?> UpdateAsync(long id, CalcJobSaveRequest request, string? changedBy, CancellationToken ct = default);

    /// <summary>
    /// Удаляет задание и его входы, выходы и состояние.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}