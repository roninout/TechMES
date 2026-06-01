namespace TechMES.Application.Param;

/// <summary>
/// Настройки Param write-flow.
/// Они лежат в Runtime.Service appsettings.json, чтобы реальную запись можно было
/// включать отдельно от разработки WEB UI.
/// </summary>
public sealed class ParamWriteOptions
{
    /// <summary>
    /// Главный выключатель write endpoint.
    /// Если false, backend отклоняет запись даже до CtApi AllowWrites.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Режим проверки без TagWrite. Удобен для отладки UI и allow-list.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Требовать комментарий оператора перед записью.
    /// </summary>
    public bool RequireComment { get; set; } = true;

    /// <summary>
    /// Вызывать audit через Cicode SaveActionOperators после успешной реальной записи.
    /// </summary>
    public bool AuditEnabled { get; set; } = true;
}
