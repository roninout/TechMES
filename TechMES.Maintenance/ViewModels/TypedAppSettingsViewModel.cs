namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Typed-представление ключевых appsettings WEB и Runtime.
/// Эта модель намеренно хранит только эксплуатационные настройки, которые оператор обслуживания
/// должен менять через форму: адреса, CtApi/PostgreSQL, write-флаги, WEB Runtime endpoint и файловые логи.
/// Полный JSON остается доступен на соседней вкладке Raw appsettings JSON.
/// </summary>
public sealed class TypedAppSettingsViewModel : ObservableObject
{
    private string _status = "";
    private string _runtimeUrls = "";
    private string _runtimeDeviceName = "";
    private string _runtimeUserName = "";
    private string _runtimeEquipmentProvider = "";
    private string _runtimeCtApiTableName = "";
    private string _runtimeCtApiFilter = "";
    private string _runtimeCtApiCluster = "";
    private bool _runtimeUseCtApiTypeFilter;
    private string _ctApiPath = "";
    private string _ctApiServer = "";
    private string _ctApiUser = "";
    private string _ctApiPassword = "";
    private int _ctApiHealthCheckPeriodSeconds;
    private int _ctApiTagReadParallelism;
    private bool _ctApiAllowWrites;
    private string _ctApiHealthCheckTag = "";
    private string _runtimeDatabaseConnectionString = "";
    private string _runtimeEventDatabaseConnectionString = "";
    private bool _paramWritesEnabled;
    private bool _paramWritesDryRun;
    private bool _paramWritesRequireComment;
    private bool _paramWritesAuditEnabled;
    private bool _runtimeFileLoggingEnabled;
    private string _runtimeFileLoggingMinimumLevel = "";
    private string _runtimeFileLoggingDirectory = "";
    private string _runtimeFileLoggingPrefix = "";
    private string _webUrls = "";
    private string _webRuntimeBaseUrl = "";
    private int _webRuntimeTimeoutSeconds;
    private string _webMessagesHubPath = "";
    private string _webDeviceName = "";
    private string _webTitle = "";
    private int _webTrendWindowMinutes;
    private int _webTrendHistoryMinutes;
    private bool _webConfirmWrites;
    private bool _webShowDeleteButton;
    private bool _webHttpsRedirectionEnabled;
    private bool _webFileLoggingEnabled;
    private string _webFileLoggingMinimumLevel = "";
    private string _webFileLoggingDirectory = "";
    private string _webFileLoggingPrefix = "";

    /// <summary>
    /// Статус последней загрузки или сохранения typed appsettings.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Runtime Urls, которые слушает Kestrel Runtime.Service.
    /// </summary>
    public string RuntimeUrls
    {
        get => _runtimeUrls;
        set => SetProperty(ref _runtimeUrls, value);
    }

    /// <summary>
    /// Имя устройства Runtime для audit/logging и служебных записей.
    /// </summary>
    public string RuntimeDeviceName
    {
        get => _runtimeDeviceName;
        set => SetProperty(ref _runtimeDeviceName, value);
    }

    /// <summary>
    /// Отображаемый пользователь Runtime.
    /// </summary>
    public string RuntimeUserName
    {
        get => _runtimeUserName;
        set => SetProperty(ref _runtimeUserName, value);
    }

    /// <summary>
    /// Провайдер каталога оборудования. Сейчас рабочий режим обычно CtApi.
    /// </summary>
    public string RuntimeEquipmentProvider
    {
        get => _runtimeEquipmentProvider;
        set => SetProperty(ref _runtimeEquipmentProvider, value);
    }

    /// <summary>
    /// Имя CtApi-таблицы оборудования.
    /// </summary>
    public string RuntimeCtApiTableName
    {
        get => _runtimeCtApiTableName;
        set => SetProperty(ref _runtimeCtApiTableName, value);
    }

    /// <summary>
    /// Фильтр CtApi для каталога оборудования.
    /// </summary>
    public string RuntimeCtApiFilter
    {
        get => _runtimeCtApiFilter;
        set => SetProperty(ref _runtimeCtApiFilter, value);
    }

    /// <summary>
    /// CtApi cluster для каталога оборудования.
    /// </summary>
    public string RuntimeCtApiCluster
    {
        get => _runtimeCtApiCluster;
        set => SetProperty(ref _runtimeCtApiCluster, value);
    }

    /// <summary>
    /// Включает серверную фильтрацию типов оборудования через CtApi.
    /// </summary>
    public bool RuntimeUseCtApiTypeFilter
    {
        get => _runtimeUseCtApiTypeFilter;
        set => SetProperty(ref _runtimeUseCtApiTypeFilter, value);
    }

    /// <summary>
    /// Путь к CtApi runtime/bin.
    /// </summary>
    public string CtApiPath
    {
        get => _ctApiPath;
        set => SetProperty(ref _ctApiPath, value);
    }

    /// <summary>
    /// Адрес Citect/CtApi сервера.
    /// </summary>
    public string CtApiServer
    {
        get => _ctApiServer;
        set => SetProperty(ref _ctApiServer, value);
    }

    /// <summary>
    /// Пользователь CtApi.
    /// </summary>
    public string CtApiUser
    {
        get => _ctApiUser;
        set => SetProperty(ref _ctApiUser, value);
    }

    /// <summary>
    /// Пароль CtApi. На текущем этапе секреты оставлены открытыми по договоренности.
    /// </summary>
    public string CtApiPassword
    {
        get => _ctApiPassword;
        set => SetProperty(ref _ctApiPassword, value);
    }

    /// <summary>
    /// Период health-check CtApi.
    /// </summary>
    public int CtApiHealthCheckPeriodSeconds
    {
        get => _ctApiHealthCheckPeriodSeconds;
        set => SetProperty(ref _ctApiHealthCheckPeriodSeconds, value);
    }

    /// <summary>
    /// Ограничение параллельного чтения тегов CtApi.
    /// </summary>
    public int CtApiTagReadParallelism
    {
        get => _ctApiTagReadParallelism;
        set => SetProperty(ref _ctApiTagReadParallelism, value);
    }

    /// <summary>
    /// Разрешает write-flow Runtime в Citect.
    /// </summary>
    public bool CtApiAllowWrites
    {
        get => _ctApiAllowWrites;
        set => SetProperty(ref _ctApiAllowWrites, value);
    }

    /// <summary>
    /// Тег для проверки доступности CtApi.
    /// </summary>
    public string CtApiHealthCheckTag
    {
        get => _ctApiHealthCheckTag;
        set => SetProperty(ref _ctApiHealthCheckTag, value);
    }

    /// <summary>
    /// PostgreSQL connection string основной БД.
    /// </summary>
    public string RuntimeDatabaseConnectionString
    {
        get => _runtimeDatabaseConnectionString;
        set => SetProperty(ref _runtimeDatabaseConnectionString, value);
    }

    /// <summary>
    /// PostgreSQL connection string БД событий.
    /// </summary>
    public string RuntimeEventDatabaseConnectionString
    {
        get => _runtimeEventDatabaseConnectionString;
        set => SetProperty(ref _runtimeEventDatabaseConnectionString, value);
    }

    /// <summary>
    /// Глобально включает запись Param.
    /// </summary>
    public bool ParamWritesEnabled
    {
        get => _paramWritesEnabled;
        set => SetProperty(ref _paramWritesEnabled, value);
    }

    /// <summary>
    /// DryRun-режим Param write: диалог и flow работают, но реальная запись не выполняется.
    /// </summary>
    public bool ParamWritesDryRun
    {
        get => _paramWritesDryRun;
        set => SetProperty(ref _paramWritesDryRun, value);
    }

    /// <summary>
    /// Требовать комментарий при записи Param.
    /// </summary>
    public bool ParamWritesRequireComment
    {
        get => _paramWritesRequireComment;
        set => SetProperty(ref _paramWritesRequireComment, value);
    }

    /// <summary>
    /// Включает audit через Cicode SaveActionOperators.
    /// </summary>
    public bool ParamWritesAuditEnabled
    {
        get => _paramWritesAuditEnabled;
        set => SetProperty(ref _paramWritesAuditEnabled, value);
    }

    /// <summary>
    /// Включает файловые логи Runtime.
    /// </summary>
    public bool RuntimeFileLoggingEnabled
    {
        get => _runtimeFileLoggingEnabled;
        set => SetProperty(ref _runtimeFileLoggingEnabled, value);
    }

    /// <summary>
    /// Минимальный уровень файлового лога Runtime.
    /// </summary>
    public string RuntimeFileLoggingMinimumLevel
    {
        get => _runtimeFileLoggingMinimumLevel;
        set => SetProperty(ref _runtimeFileLoggingMinimumLevel, value);
    }

    /// <summary>
    /// Папка файлового лога Runtime относительно папки сервиса или абсолютный путь.
    /// </summary>
    public string RuntimeFileLoggingDirectory
    {
        get => _runtimeFileLoggingDirectory;
        set => SetProperty(ref _runtimeFileLoggingDirectory, value);
    }

    /// <summary>
    /// Префикс имени файла Runtime log.
    /// </summary>
    public string RuntimeFileLoggingPrefix
    {
        get => _runtimeFileLoggingPrefix;
        set => SetProperty(ref _runtimeFileLoggingPrefix, value);
    }

    /// <summary>
    /// WEB Urls, которые слушает Kestrel WEB.
    /// </summary>
    public string WebUrls
    {
        get => _webUrls;
        set => SetProperty(ref _webUrls, value);
    }

    /// <summary>
    /// Base URL Runtime.Service для WEB.
    /// </summary>
    public string WebRuntimeBaseUrl
    {
        get => _webRuntimeBaseUrl;
        set => SetProperty(ref _webRuntimeBaseUrl, value);
    }

    /// <summary>
    /// HTTP timeout WEB -> Runtime.Service.
    /// </summary>
    public int WebRuntimeTimeoutSeconds
    {
        get => _webRuntimeTimeoutSeconds;
        set => SetProperty(ref _webRuntimeTimeoutSeconds, value);
    }

    /// <summary>
    /// SignalR hub path сообщений.
    /// </summary>
    public string WebMessagesHubPath
    {
        get => _webMessagesHubPath;
        set => SetProperty(ref _webMessagesHubPath, value);
    }

    /// <summary>
    /// Имя WEB-клиента для сообщений и audit-отображения.
    /// </summary>
    public string WebDeviceName
    {
        get => _webDeviceName;
        set => SetProperty(ref _webDeviceName, value);
    }

    /// <summary>
    /// Заголовок WEB-приложения.
    /// </summary>
    public string WebTitle
    {
        get => _webTitle;
        set => SetProperty(ref _webTitle, value);
    }

    /// <summary>
    /// Видимое окно тренда Param в минутах.
    /// </summary>
    public int WebTrendWindowMinutes
    {
        get => _webTrendWindowMinutes;
        set => SetProperty(ref _webTrendWindowMinutes, value);
    }

    /// <summary>
    /// Загружаемая история тренда Param в минутах.
    /// </summary>
    public int WebTrendHistoryMinutes
    {
        get => _webTrendHistoryMinutes;
        set => SetProperty(ref _webTrendHistoryMinutes, value);
    }

    /// <summary>
    /// Включает подтверждение записи Param на стороне WEB.
    /// </summary>
    public bool WebConfirmWrites
    {
        get => _webConfirmWrites;
        set => SetProperty(ref _webConfirmWrites, value);
    }

    /// <summary>
    /// Показывать кнопку Delete во вкладке Messages.
    /// </summary>
    public bool WebShowDeleteButton
    {
        get => _webShowDeleteButton;
        set => SetProperty(ref _webShowDeleteButton, value);
    }

    /// <summary>
    /// Включает HTTP -> HTTPS redirect в WEB.
    /// </summary>
    public bool WebHttpsRedirectionEnabled
    {
        get => _webHttpsRedirectionEnabled;
        set => SetProperty(ref _webHttpsRedirectionEnabled, value);
    }

    /// <summary>
    /// Включает файловые логи WEB.
    /// </summary>
    public bool WebFileLoggingEnabled
    {
        get => _webFileLoggingEnabled;
        set => SetProperty(ref _webFileLoggingEnabled, value);
    }

    /// <summary>
    /// Минимальный уровень файлового лога WEB.
    /// </summary>
    public string WebFileLoggingMinimumLevel
    {
        get => _webFileLoggingMinimumLevel;
        set => SetProperty(ref _webFileLoggingMinimumLevel, value);
    }

    /// <summary>
    /// Папка файлового лога WEB относительно папки сервиса или абсолютный путь.
    /// </summary>
    public string WebFileLoggingDirectory
    {
        get => _webFileLoggingDirectory;
        set => SetProperty(ref _webFileLoggingDirectory, value);
    }

    /// <summary>
    /// Префикс имени файла WEB log.
    /// </summary>
    public string WebFileLoggingPrefix
    {
        get => _webFileLoggingPrefix;
        set => SetProperty(ref _webFileLoggingPrefix, value);
    }
}
