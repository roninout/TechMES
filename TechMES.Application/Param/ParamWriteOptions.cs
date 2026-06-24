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

    /// <summary>
    /// Проверка Windows-пользователя и Windows-групп перед записью.
    /// Runtime.Service выполняет эту проверку последним рубежом, даже если WEB уже спрятал UI.
    /// </summary>
    public ParamWriteAuthorizationOptions Authorization { get; set; } = new();
}

/// <summary>
/// Allow-list Windows-пользователей и групп, которым разрешен Param write-flow.
/// WEB передает Runtime текущий Windows account и список групп, а Runtime сравнивает их с этим профилем.
/// </summary>
public sealed class ParamWriteAuthorizationOptions
{
    /// <summary>
    /// Включает принудительную проверку allow-list перед каждым write-запросом.
    /// Если false, Runtime работает по прежней схеме: Enabled/DryRun/CtApi AllowWrites.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Требовать, чтобы WEB передал Windows-пользователя.
    /// Обычно это true; выключать имеет смысл только для сервисной отладки.
    /// </summary>
    public bool RequireWindowsUser { get; set; } = true;

    /// <summary>
    /// Разрешенные Windows-пользователи через ';' или ','.
    /// Поддерживаются варианты DOMAIN\User и короткое имя User.
    /// </summary>
    public string AllowedUsers { get; set; } = "";

    /// <summary>
    /// Разрешенные Windows-группы через ';' или ','.
    /// Поддерживаются варианты DOMAIN\Group и короткое имя Group.
    /// </summary>
    public string AllowedGroups { get; set; } = "";
}
