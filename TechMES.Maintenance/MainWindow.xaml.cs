using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance;

/// <summary>
/// Главное окно обслуживающего приложения TechMES.
/// Первый инкремент закрывает базовую эксплуатационную задачу:
/// увидеть сервисы, проверить health URL, открыть appsettings, сохранить JSON с backup и посмотреть логи.
/// </summary>
public partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions TypedAppSettingsJsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly DirectoryInfo _repositoryRoot;
    private readonly MaintenanceConfiguration _configuration;
    private readonly MaintenanceConfigurationStore _configurationStore;
    private readonly WindowsServiceManager _serviceManager;
    private readonly DeploymentManager _deploymentManager;
    private readonly HealthProbeService _healthProbeService = new();
    private readonly SettingsFileService _settingsFileService = new();
    private readonly LogFileService _logFileService;
    private readonly ServerNetworkService _serverNetworkService = new();
    private readonly WindowsFirewallManager _firewallManager;
    private readonly HttpsCertificateManager _httpsCertificateManager;
    private readonly WebHttpsConfigurator _webHttpsConfigurator;
    private readonly BackupRestoreService _backupRestoreService;
    private readonly CleanupService _cleanupService;
    private readonly PostgreSqlProbeService _postgreSqlProbeService = new();

    private string _diagnosticsText = "";
    private string _deploymentLogText = "";
    private SettingsFileViewModel? _selectedSettingsFile;
    private LogFileViewModel? _selectedLogFile;
    private string _selectedLogText = "";
    private DateTime? _logSelectedDate = DateTime.Today;
    private string _serverPortStatus = "";
    private string _serverWebPortState = "Not checked";
    private string _serverHttpsPortState = "Not checked";
    private string _serverRuntimePortState = "Not checked";
    private string _serverFirewallStatus = "";
    private string _serverHttpsFirewallStatus = "";
    private string _serverCertificateStatus = "";
    private string _serverLogText = "";
    private string _dependencyCheckStatusText = "";
    private string _windowsAuthDiagnosticsStatusText = "Not checked.";
    private string _operatorActionDiagnosticsStatusText = "Not checked.";
    private string _serverProfileReadinessStatus = "Not checked";
    private string _serverProfileReadinessDetails = "Refresh server profile to calculate readiness.";
    private string _serverProfileLastUpdated = "";
    private string _backupRoot = "";
    private string _backupStatusText = "No backup operation has been started.";
    private string _cleanupStatusText = "Cleanup scan has not been started.";
    private BackupItemViewModel? _selectedBackup;

    /// <summary>
    /// Результат проверки DNS/hosts-алиаса сервера.
    /// Используется в Checks и Server profile, чтобы обе вкладки одинаково трактовали поле Host name.
    /// </summary>
    private sealed record HostResolutionStatus(
        string Status,
        string Details,
        IReadOnlyList<string> ResolvedIps);

    /// <summary>
    /// Событие WPF binding для свойств окна.
    /// Для первого этапа это проще, чем полноценный MVVM-фреймворк.
    /// </summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Строки таблицы сервисов.
    /// </summary>
    public ObservableCollection<ServiceStatusViewModel> ServiceStatuses { get; } = [];

    /// <summary>
    /// Список известных appsettings-файлов.
    /// </summary>
    public ObservableCollection<SettingsFileViewModel> SettingsFiles { get; } = [];

    /// <summary>
    /// Список сервисов, которые участвуют в серверном развертывании.
    /// </summary>
    public ObservableCollection<DeploymentServiceViewModel> DeploymentServices { get; } = [];

    /// <summary>
    /// Найденные log-файлы.
    /// </summary>
    public ObservableCollection<LogFileViewModel> LogFiles { get; } = [];

    /// <summary>
    /// Результаты диагностических проверок внешних зависимостей и файлового окружения.
    /// Эти проверки ничего не меняют в системе: они только помогают понять, готов ли сервер к работе.
    /// </summary>
    public ObservableCollection<DependencyCheckViewModel> DependencyChecks { get; } = [];

    /// <summary>
    /// Read-only Windows-auth diagnostics split out from the generic Checks tab.
    /// </summary>
    public ObservableCollection<DependencyCheckViewModel> WindowsAuthDiagnostics { get; } = [];

    /// <summary>
    /// Read-only diagnostics for the Param write audit chain and Operation actions visibility.
    /// </summary>
    public ObservableCollection<DependencyCheckViewModel> OperatorActionDiagnostics { get; } = [];

    /// <summary>
    /// Понятный профиль текущего сервера: сеть, порты, сертификат, firewall, publish и Windows Services.
    /// Это не отдельная конфигурация, а вычисляемая карточка поверх maintenance.settings.json и живых статусов.
    /// </summary>
    public ObservableCollection<ServerProfileItemViewModel> ServerProfileItems { get; } = [];

    /// <summary>
    /// Список найденных backup-снимков. Строки строятся по backup-manifest.json,
    /// поэтому оператор видит только валидные и понятные снимки конфигурации.
    /// </summary>
    public ObservableCollection<BackupItemViewModel> BackupItems { get; } = [];

    /// <summary>
    /// Кандидаты на безопасную ручную очистку: старые логи, side-backup appsettings и старые backup-папки.
    /// Удаление запускается только кнопкой оператора, автоматической очистки нет.
    /// </summary>
    public ObservableCollection<CleanupItemViewModel> CleanupItems { get; } = [];

    /// <summary>
    /// Свободное место на дисках, где находятся репозиторий, publish root и backup root.
    /// </summary>
    public ObservableCollection<DiskStatusViewModel> DiskStatuses { get; } = [];

    /// <summary>
    /// Typed-поля для appsettings WEB и Runtime.
    /// Эти значения пишутся в appsettings.json и начинают работать после рестарта соответствующей службы.
    /// </summary>
    public TypedAppSettingsViewModel TypedAppSettings { get; } = new();

    /// <summary>
    /// Короткая строка состояния вкладки Checks.
    /// </summary>
    public string DependencyCheckStatusText
    {
        get => _dependencyCheckStatusText;
        set
        {
            if (_dependencyCheckStatusText == value)
                return;

            _dependencyCheckStatusText = value;
            OnPropertyChanged(nameof(DependencyCheckStatusText));
        }
    }

    /// <summary>
    /// Short status text for the Windows auth diagnostics tab.
    /// </summary>
    public string WindowsAuthDiagnosticsStatusText
    {
        get => _windowsAuthDiagnosticsStatusText;
        set
        {
            if (_windowsAuthDiagnosticsStatusText == value)
                return;

            _windowsAuthDiagnosticsStatusText = value;
            OnPropertyChanged(nameof(WindowsAuthDiagnosticsStatusText));
        }
    }

    /// <summary>
    /// Short status text for the Operator actions diagnostics tab.
    /// </summary>
    public string OperatorActionDiagnosticsStatusText
    {
        get => _operatorActionDiagnosticsStatusText;
        set
        {
            if (_operatorActionDiagnosticsStatusText == value)
                return;

            _operatorActionDiagnosticsStatusText = value;
            OnPropertyChanged(nameof(OperatorActionDiagnosticsStatusText));
        }
    }

    /// <summary>
    /// Итоговая готовность сервера по профилю.
    /// </summary>
    public string ServerProfileReadinessStatus
    {
        get => _serverProfileReadinessStatus;
        set
        {
            if (_serverProfileReadinessStatus == value)
                return;

            _serverProfileReadinessStatus = value;
            OnPropertyChanged(nameof(ServerProfileReadinessStatus));
        }
    }

    /// <summary>
    /// Причины итогового статуса готовности.
    /// </summary>
    public string ServerProfileReadinessDetails
    {
        get => _serverProfileReadinessDetails;
        set
        {
            if (_serverProfileReadinessDetails == value)
                return;

            _serverProfileReadinessDetails = value;
            OnPropertyChanged(nameof(ServerProfileReadinessDetails));
        }
    }

    /// <summary>
    /// Время последнего пересчета профиля.
    /// </summary>
    public string ServerProfileLastUpdated
    {
        get => _serverProfileLastUpdated;
        set
        {
            if (_serverProfileLastUpdated == value)
                return;

            _serverProfileLastUpdated = value;
            OnPropertyChanged(nameof(ServerProfileLastUpdated));
        }
    }

    /// <summary>
    /// Папка, где Maintenance создает timestamp-папки backup-снимков.
    /// Ее можно изменить прямо в UI перед созданием или восстановлением.
    /// </summary>
    public string BackupRoot
    {
        get => _backupRoot;
        set
        {
            if (_backupRoot == value)
                return;

            _backupRoot = value;
            _configuration.Cleanup.BackupRoot = value;
            OnPropertyChanged(nameof(BackupRoot));
        }
    }

    /// <summary>
    /// День, за который показываются log-файлы во вкладке Logs.
    /// По умолчанию выбран сегодняшний день, чтобы оператор сразу видел актуальные журналы.
    /// </summary>
    public DateTime? LogSelectedDate
    {
        get => _logSelectedDate;
        set
        {
            if (_logSelectedDate == value)
                return;

            _logSelectedDate = value;
            OnPropertyChanged(nameof(LogSelectedDate));
        }
    }

    /// <summary>
    /// Текущий выбранный backup-снимок для операции restore.
    /// </summary>
    public BackupItemViewModel? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            _selectedBackup = value;
            OnPropertyChanged(nameof(SelectedBackup));
        }
    }

    /// <summary>
    /// Короткое состояние последней операции Backup / Restore.
    /// </summary>
    public string BackupStatusText
    {
        get => _backupStatusText;
        set
        {
            if (_backupStatusText == value)
                return;

            _backupStatusText = value;
            OnPropertyChanged(nameof(BackupStatusText));
        }
    }

    /// <summary>
    /// Короткий итог последнего сканирования или удаления во вкладке Service actions.
    /// </summary>
    public string CleanupStatusText
    {
        get => _cleanupStatusText;
        set
        {
            if (_cleanupStatusText == value)
                return;

            _cleanupStatusText = value;
            OnPropertyChanged(nameof(CleanupStatusText));
        }
    }

    /// <summary>
    /// Сетевые адреса, по которым WEB можно открыть с планшета или другого клиента.
    /// </summary>
    public ObservableCollection<ServerAddressInfo> ServerAddresses { get; } = [];

    /// <summary>
    /// Корень репозитория, который нашло приложение.
    /// </summary>
    public string RepositoryRootText => $"Repository: {_repositoryRoot.FullName}";

    /// <summary>
    /// Полный путь к корню репозитория без префикса, удобный для отображения в настройках.
    /// </summary>
    public string RepositoryRootPath => _repositoryRoot.FullName;

    /// <summary>
    /// WEB-порт серверного режима.
    /// </summary>
    public int ServerWebPort
    {
        get => _configuration.Server.WebPort;
        set
        {
            if (_configuration.Server.WebPort == value)
                return;

            _configuration.Server.WebPort = value;
            OnPropertyChanged(nameof(ServerWebPort));
            OnPropertyChanged(nameof(ServerWebPortLine));
            RefreshServerAddressRows();
        }
    }

    /// <summary>
    /// Runtime-порт серверного режима. Он нужен для локальной связи WEB -> Runtime.
    /// </summary>
    public int ServerRuntimePort
    {
        get => _configuration.Server.RuntimePort;
        set
        {
            if (_configuration.Server.RuntimePort == value)
                return;

            _configuration.Server.RuntimePort = value;
            OnPropertyChanged(nameof(ServerRuntimePort));
            OnPropertyChanged(nameof(ServerRuntimePortLine));
        }
    }

    /// <summary>
    /// Имя правила Windows Firewall, которое открывает WEB-порт.
    /// </summary>
    public string ServerFirewallRuleName
    {
        get => _configuration.Server.FirewallRuleName;
        set
        {
            if (_configuration.Server.FirewallRuleName == value)
                return;

            _configuration.Server.FirewallRuleName = value;
            OnPropertyChanged(nameof(ServerFirewallRuleName));
            OnPropertyChanged(nameof(ServerFirewallRuleLine));
        }
    }

    /// <summary>
    /// HTTPS-порт WEB, который будет использоваться для камеры, QR-сканера и других browser API на планшетах.
    /// </summary>
    public int ServerHttpsPort
    {
        get => _configuration.Server.HttpsPort;
        set
        {
            if (_configuration.Server.HttpsPort == value)
                return;

            _configuration.Server.HttpsPort = value;
            OnPropertyChanged(nameof(ServerHttpsPort));
            OnPropertyChanged(nameof(ServerHttpsPortLine));
            RefreshServerAddressRows();
        }
    }

    /// <summary>
    /// Имя firewall-правила для HTTPS-порта WEB.
    /// </summary>
    public string ServerHttpsFirewallRuleName
    {
        get => _configuration.Server.HttpsFirewallRuleName;
        set
        {
            if (_configuration.Server.HttpsFirewallRuleName == value)
                return;

            _configuration.Server.HttpsFirewallRuleName = value;
            OnPropertyChanged(nameof(ServerHttpsFirewallRuleName));
            OnPropertyChanged(nameof(ServerHttpsFirewallRuleLine));
        }
    }

    /// <summary>
    /// Папка PFX/CER сертификатов. Поле редактируется в typed Settings.
    /// </summary>
    public string ServerCertificateDirectory
    {
        get => _configuration.Server.CertificateDirectory;
        set
        {
            if (_configuration.Server.CertificateDirectory == value)
                return;

            _configuration.Server.CertificateDirectory = value;
            OnPropertyChanged(nameof(ServerCertificateDirectory));
            RefreshCertificatePathBindings();
        }
    }

    /// <summary>
    /// Имя PFX-файла, который подключается Kestrel.
    /// </summary>
    public string ServerCertificateFileName
    {
        get => _configuration.Server.CertificateFileName;
        set
        {
            if (_configuration.Server.CertificateFileName == value)
                return;

            _configuration.Server.CertificateFileName = value;
            OnPropertyChanged(nameof(ServerCertificateFileName));
            RefreshCertificatePathBindings();
        }
    }

    /// <summary>
    /// Имя публичного CER-файла для установки доверия на клиентах.
    /// </summary>
    public string ServerPublicCertificateFileName
    {
        get => _configuration.Server.PublicCertificateFileName;
        set
        {
            if (_configuration.Server.PublicCertificateFileName == value)
                return;

            _configuration.Server.PublicCertificateFileName = value;
            OnPropertyChanged(nameof(ServerPublicCertificateFileName));
            RefreshCertificatePathBindings();
        }
    }

    /// <summary>
    /// Пароль PFX. На текущем этапе секреты оставлены открытыми по договоренности.
    /// </summary>
    public string ServerCertificatePassword
    {
        get => _configuration.Server.CertificatePassword;
        set
        {
            if (_configuration.Server.CertificatePassword == value)
                return;

            _configuration.Server.CertificatePassword = value;
            OnPropertyChanged(nameof(ServerCertificatePassword));
        }
    }

    /// <summary>
    /// Subject self-signed сертификата.
    /// </summary>
    public string ServerCertificateSubject
    {
        get => _configuration.Server.CertificateSubject;
        set
        {
            if (_configuration.Server.CertificateSubject == value)
                return;

            _configuration.Server.CertificateSubject = value;
            OnPropertyChanged(nameof(ServerCertificateSubject));
        }
    }

    /// <summary>
    /// Включать ли HTTP -> HTTPS redirect в WEB.
    /// </summary>
    public bool ServerEnableHttpsRedirection
    {
        get => _configuration.Server.EnableHttpsRedirection;
        set
        {
            if (_configuration.Server.EnableHttpsRedirection == value)
                return;

            _configuration.Server.EnableHttpsRedirection = value;
            OnPropertyChanged(nameof(ServerEnableHttpsRedirection));
        }
    }

    /// <summary>
    /// Полный путь к PFX-файлу, который подключается в Kestrel.
    /// </summary>
    public string ServerCertificatePfxPath => HttpsCertificateManager.GetPfxPath(_configuration.Server);

    /// <summary>
    /// Полный путь к CER-файлу, который нужно доверить на клиентском устройстве.
    /// </summary>
    public string ServerPublicCertificatePath => HttpsCertificateManager.GetPublicCertificatePath(_configuration.Server);

    /// <summary>
    /// Сколько дней хранить опубликованные log-файлы перед ручной очисткой.
    /// </summary>
    public int CleanupLogRetentionDays
    {
        get => _configuration.Cleanup.LogRetentionDays;
        set
        {
            if (_configuration.Cleanup.LogRetentionDays == value)
                return;

            _configuration.Cleanup.LogRetentionDays = value;
            OnPropertyChanged(nameof(CleanupLogRetentionDays));
        }
    }

    /// <summary>
    /// Сколько дней хранить служебные .bak и .restore-backup файлы appsettings.
    /// </summary>
    public int CleanupAppSettingsBackupRetentionDays
    {
        get => _configuration.Cleanup.AppSettingsBackupRetentionDays;
        set
        {
            if (_configuration.Cleanup.AppSettingsBackupRetentionDays == value)
                return;

            _configuration.Cleanup.AppSettingsBackupRetentionDays = value;
            OnPropertyChanged(nameof(CleanupAppSettingsBackupRetentionDays));
        }
    }

    /// <summary>
    /// Сколько дней хранить backup-снимки Maintenance перед ручной очисткой.
    /// </summary>
    public int CleanupBackupRetentionDays
    {
        get => _configuration.Cleanup.BackupRetentionDays;
        set
        {
            if (_configuration.Cleanup.BackupRetentionDays == value)
                return;

            _configuration.Cleanup.BackupRetentionDays = value;
            OnPropertyChanged(nameof(CleanupBackupRetentionDays));
        }
    }

    /// <summary>
    /// Человекочитаемое имя целевого сервера.
    /// </summary>
    public string TargetMachineDisplayName
    {
        get => _configuration.TargetMachine.DisplayName;
        set
        {
            if (_configuration.TargetMachine.DisplayName == value)
                return;

            _configuration.TargetMachine.DisplayName = value;
            OnPropertyChanged(nameof(TargetMachineDisplayName));
        }
    }

    /// <summary>
    /// Ожидаемое имя Windows-машины, чтобы Maintenance мог предупредить о запуске не на том сервере.
    /// </summary>
    public string TargetMachineHostName
    {
        get => _configuration.TargetMachine.HostName;
        set
        {
            if (_configuration.TargetMachine.HostName == value)
                return;

            _configuration.TargetMachine.HostName = value;
            OnPropertyChanged(nameof(TargetMachineHostName));
        }
    }

    /// <summary>
    /// Ожидаемый IPv4-адрес WEB-сервера для планшетов и клиентских машин.
    /// </summary>
    public string TargetMachineIpAddress
    {
        get => _configuration.TargetMachine.IpAddress;
        set
        {
            if (_configuration.TargetMachine.IpAddress == value)
                return;

            _configuration.TargetMachine.IpAddress = value;
            OnPropertyChanged(nameof(TargetMachineIpAddress));
        }
    }

    /// <summary>
    /// Свободная заметка о целевом сервере: где стоит, кто обслуживает, особенности доступа.
    /// </summary>
    public string TargetMachineNotes
    {
        get => _configuration.TargetMachine.Notes;
        set
        {
            if (_configuration.TargetMachine.Notes == value)
                return;

            _configuration.TargetMachine.Notes = value;
            OnPropertyChanged(nameof(TargetMachineNotes));
        }
    }

    /// <summary>
    /// Включен ли будущий режим проверки Windows-пользователей.
    /// Пока это профиль готовности, а не enforcement внутри WEB/Runtime.
    /// </summary>
    public bool SecurityWindowsUsersEnabled
    {
        get => _configuration.Security.WindowsUsersEnabled;
        set
        {
            if (_configuration.Security.WindowsUsersEnabled == value)
                return;

            _configuration.Security.WindowsUsersEnabled = value;
            OnPropertyChanged(nameof(SecurityWindowsUsersEnabled));
        }
    }

    /// <summary>
    /// Windows-группы, которым в будущем будет разрешен write-режим.
    /// </summary>
    public string SecurityWriteGroups
    {
        get => _configuration.Security.WriteGroups;
        set
        {
            if (_configuration.Security.WriteGroups == value)
                return;

            _configuration.Security.WriteGroups = value;
            OnPropertyChanged(nameof(SecurityWriteGroups));
        }
    }

    /// <summary>
    /// Требовать ли подтверждение write-операции в WEB.
    /// Поле сверяется с реальным WEB appsettings во вкладке Checks.
    /// </summary>
    public bool SecurityRequireWriteConfirmation
    {
        get => _configuration.Security.RequireWriteConfirmation;
        set
        {
            if (_configuration.Security.RequireWriteConfirmation == value)
                return;

            _configuration.Security.RequireWriteConfirmation = value;
            OnPropertyChanged(nameof(SecurityRequireWriteConfirmation));
        }
    }

    /// <summary>
    /// Требовать ли SCADA-аудит write-операций через SaveActionOperators.
    /// </summary>
    public bool SecurityRequireScadaAudit
    {
        get => _configuration.Security.RequireScadaAudit;
        set
        {
            if (_configuration.Security.RequireScadaAudit == value)
                return;

            _configuration.Security.RequireScadaAudit = value;
            OnPropertyChanged(nameof(SecurityRequireScadaAudit));
        }
    }

    /// <summary>
    /// Health URL Runtime.Service в Dashboard.
    /// </summary>
    public string RuntimeHealthUrl
    {
        get => GetServiceDefinition("runtime")?.HealthUrl ?? "";
        set => SetServiceHealthUrl("runtime", value, nameof(RuntimeHealthUrl));
    }

    /// <summary>
    /// Health URL TechMES.Web в Dashboard.
    /// </summary>
    public string WebHealthUrl
    {
        get => GetServiceDefinition("web")?.HealthUrl ?? "";
        set => SetServiceHealthUrl("web", value, nameof(WebHealthUrl));
    }

    /// <summary>
    /// Локальный статус прослушивания WEB/Runtime портов.
    /// </summary>
    public string ServerPortStatus
    {
        get => _serverPortStatus;
        set
        {
            _serverPortStatus = value;
            OnPropertyChanged(nameof(ServerPortStatus));
        }
    }

    /// <summary>
    /// Отдельная строка состояния HTTP-порта WEB в блоке Server checks.
    /// </summary>
    public string ServerWebPortLine => $"{ServerWebPort} : {_serverWebPortState}";

    /// <summary>
    /// Отдельная строка состояния HTTPS-порта WEB в блоке Server checks.
    /// </summary>
    public string ServerHttpsPortLine => $"{ServerHttpsPort} : {_serverHttpsPortState}";

    /// <summary>
    /// Отдельная строка состояния Runtime-порта в блоке Server checks.
    /// </summary>
    public string ServerRuntimePortLine => $"{ServerRuntimePort} : {_serverRuntimePortState}";

    /// <summary>
    /// HTTP firewall-правило вместе с текущим статусом.
    /// </summary>
    public string ServerFirewallRuleLine => $"{ServerFirewallRuleName} : {(string.IsNullOrWhiteSpace(ServerFirewallStatus) ? "Not checked" : ServerFirewallStatus)}";

    /// <summary>
    /// HTTPS firewall-правило вместе с текущим статусом.
    /// </summary>
    public string ServerHttpsFirewallRuleLine => $"{ServerHttpsFirewallRuleName} : {(string.IsNullOrWhiteSpace(ServerHttpsFirewallStatus) ? "Not checked" : ServerHttpsFirewallStatus)}";

    /// <summary>
    /// Статус входящего правила Windows Firewall для WEB-порта.
    /// </summary>
    public string ServerFirewallStatus
    {
        get => _serverFirewallStatus;
        set
        {
            _serverFirewallStatus = value;
            OnPropertyChanged(nameof(ServerFirewallStatus));
            OnPropertyChanged(nameof(ServerFirewallRuleLine));
        }
    }

    /// <summary>
    /// Статус HTTPS firewall-правила.
    /// </summary>
    public string ServerHttpsFirewallStatus
    {
        get => _serverHttpsFirewallStatus;
        set
        {
            _serverHttpsFirewallStatus = value;
            OnPropertyChanged(nameof(ServerHttpsFirewallStatus));
            OnPropertyChanged(nameof(ServerHttpsFirewallRuleLine));
        }
    }

    /// <summary>
    /// Статус PFX/CER сертификата.
    /// </summary>
    public string ServerCertificateStatus
    {
        get => _serverCertificateStatus;
        set
        {
            _serverCertificateStatus = value;
            OnPropertyChanged(nameof(ServerCertificateStatus));
        }
    }

    /// <summary>
    /// Журнал действий вкладки Server.
    /// </summary>
    public string ServerLogText
    {
        get => _serverLogText;
        set
        {
            _serverLogText = value;
            OnPropertyChanged(nameof(ServerLogText));
        }
    }

    /// <summary>
    /// Текст диагностического окна на Dashboard.
    /// </summary>
    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set
        {
            _diagnosticsText = value;
            OnPropertyChanged(nameof(DiagnosticsText));
        }
    }

    /// <summary>
    /// Корневая папка публикации на сервер.
    /// Обычно это C:\TechMES, но поле можно изменить и сохранить в maintenance.settings.json.
    /// </summary>
    public string DeploymentPublishRoot
    {
        get => _configuration.Deployment.PublishRoot;
        set
        {
            if (_configuration.Deployment.PublishRoot == value)
                return;

            _configuration.Deployment.PublishRoot = value;
            OnPropertyChanged(nameof(DeploymentPublishRoot));
            RefreshDeploymentPaths();
        }
    }

    /// <summary>
    /// Конфигурация dotnet publish: Debug или Release.
    /// </summary>
    public string DeploymentConfigurationName
    {
        get => _configuration.Deployment.Configuration;
        set
        {
            if (_configuration.Deployment.Configuration == value)
                return;

            _configuration.Deployment.Configuration = value;
            OnPropertyChanged(nameof(DeploymentConfigurationName));
        }
    }

    /// <summary>
    /// Runtime identifier для publish, например win-x64.
    /// </summary>
    public string DeploymentRuntimeIdentifier
    {
        get => _configuration.Deployment.RuntimeIdentifier;
        set
        {
            if (_configuration.Deployment.RuntimeIdentifier == value)
                return;

            _configuration.Deployment.RuntimeIdentifier = value;
            OnPropertyChanged(nameof(DeploymentRuntimeIdentifier));
        }
    }

    /// <summary>
    /// Публиковать сервисы self-contained.
    /// </summary>
    public bool DeploymentSelfContained
    {
        get => _configuration.Deployment.SelfContained;
        set
        {
            if (_configuration.Deployment.SelfContained == value)
                return;

            _configuration.Deployment.SelfContained = value;
            OnPropertyChanged(nameof(DeploymentSelfContained));
        }
    }

    /// <summary>
    /// Настраивать Windows Services на автоматический запуск.
    /// </summary>
    public bool DeploymentAutoStart
    {
        get => _configuration.Deployment.AutoStart;
        set
        {
            if (_configuration.Deployment.AutoStart == value)
                return;

            _configuration.Deployment.AutoStart = value;
            OnPropertyChanged(nameof(DeploymentAutoStart));
        }
    }

    /// <summary>
    /// Журнал операций вкладки Deploy.
    /// </summary>
    public string DeploymentLogText
    {
        get => _deploymentLogText;
        set
        {
            _deploymentLogText = value;
            OnPropertyChanged(nameof(DeploymentLogText));
        }
    }

    /// <summary>
    /// Выбранный appsettings-файл.
    /// </summary>
    public SettingsFileViewModel? SelectedSettingsFile
    {
        get => _selectedSettingsFile;
        set
        {
            _selectedSettingsFile = value;
            OnPropertyChanged(nameof(SelectedSettingsFile));
        }
    }

    /// <summary>
    /// Выбранный log-файл.
    /// </summary>
    public LogFileViewModel? SelectedLogFile
    {
        get => _selectedLogFile;
        set
        {
            _selectedLogFile = value;
            OnPropertyChanged(nameof(SelectedLogFile));
        }
    }

    /// <summary>
    /// Последние строки выбранного log-файла.
    /// </summary>
    public string SelectedLogText
    {
        get => _selectedLogText;
        set
        {
            _selectedLogText = value;
            OnPropertyChanged(nameof(SelectedLogText));
        }
    }

    /// <summary>
    /// Инициализирует зависимости окна без DI-контейнера.
    /// Для небольшого WPF Maintenance это пока проще и прозрачнее.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        var repositoryLocator = new RepositoryLocator();
        _repositoryRoot = repositoryLocator.LocateRepositoryRoot();

        _configurationStore = new MaintenanceConfigurationStore(_repositoryRoot);
        _configuration = _configurationStore.Load();

        _serviceManager = new WindowsServiceManager(new ProcessRunner());
        _deploymentManager = new DeploymentManager(_repositoryRoot, new ProcessRunner(), _serviceManager);
        _firewallManager = new WindowsFirewallManager(new ProcessRunner());
        _httpsCertificateManager = new HttpsCertificateManager(_serverNetworkService);
        _webHttpsConfigurator = new WebHttpsConfigurator(_repositoryRoot);
        _logFileService = new LogFileService(_repositoryRoot);
        _backupRestoreService = new BackupRestoreService(_repositoryRoot);
        _cleanupService = new CleanupService(_repositoryRoot);
        BackupRoot = string.IsNullOrWhiteSpace(_configuration.Cleanup.BackupRoot)
            ? GetDefaultBackupRoot()
            : _configuration.Cleanup.BackupRoot;

        DataContext = this;

        InitializeServiceRows();
        InitializeDeploymentRows();
        InitializeSettingsRows();
        LoadTypedAppSettings();
        RefreshServerAddressRows();
        RefreshServerProfile();
        RefreshWindowsAuthDiagnostics();
        RefreshOperatorActionDiagnostics();
        RefreshLogRows();
        RefreshBackupRows();
        RefreshDiskStatuses();
    }

    /// <summary>
    /// При открытии окна сразу обновляем статусы, чтобы пользователь видел живое состояние.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Возвращает путь backup по умолчанию, если оператор еще не сохранил свой каталог.
    /// </summary>
    private string GetDefaultBackupRoot()
    {
        return Path.Combine(_repositoryRoot.FullName, "TechMES.Maintenance", "backups");
    }

    /// <summary>
    /// Создает строки Dashboard из maintenance.settings.json.
    /// </summary>
    private void InitializeServiceRows()
    {
        ServiceStatuses.Clear();

        foreach (var service in _configuration.Services)
            ServiceStatuses.Add(new ServiceStatusViewModel(service));
    }

    /// <summary>
    /// Создает строки вкладки Deploy из тех же сервисов, что и Dashboard.
    /// Это важно: один источник конфигурации не дает разъехаться service name и publish path.
    /// </summary>
    private void InitializeDeploymentRows()
    {
        DeploymentServices.Clear();

        foreach (var service in _configuration.Services.Where(service => !string.IsNullOrWhiteSpace(service.ProjectPath)))
            DeploymentServices.Add(new DeploymentServiceViewModel(service));

        RefreshDeploymentPaths();
    }

    /// <summary>
    /// Пересчитывает publish/exe пути после изменения Deployment:PublishRoot.
    /// </summary>
    private void RefreshDeploymentPaths()
    {
        foreach (var service in DeploymentServices)
        {
            service.PublishDirectory = _deploymentManager.GetPublishDirectory(service.Definition, _configuration.Deployment);
            service.ExecutablePath = _deploymentManager.GetExecutablePath(service.Definition, _configuration.Deployment);
        }
    }

    /// <summary>
    /// Перечитывает пути сертификатов в UI после изменения папки или имен файлов.
    /// </summary>
    private void RefreshCertificatePathBindings()
    {
        OnPropertyChanged(nameof(ServerCertificatePfxPath));
        OnPropertyChanged(nameof(ServerPublicCertificatePath));
    }

    /// <summary>
    /// Ищет описание сервиса по стабильному ключу из maintenance.settings.json.
    /// </summary>
    private ServiceDefinition? GetServiceDefinition(string key) =>
        _configuration.Services.FirstOrDefault(service =>
            string.Equals(service.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Обновляет HealthUrl выбранного сервиса и уведомляет строки Dashboard.
    /// </summary>
    private void SetServiceHealthUrl(
        string serviceKey,
        string value,
        string propertyName)
    {
        var service = GetServiceDefinition(serviceKey);
        if (service is null || service.HealthUrl == value)
            return;

        service.HealthUrl = value;
        OnPropertyChanged(propertyName);

        foreach (var row in ServiceStatuses.Where(row => ReferenceEquals(row.Definition, service)))
            row.RefreshDefinitionBindings();
    }

    /// <summary>
    /// Создает список редактируемых appsettings-файлов.
    /// </summary>
    private void InitializeSettingsRows()
    {
        SettingsFiles.Clear();

        foreach (var settingsFile in _configuration.SettingsFiles)
        {
            var fullPath = Path.Combine(_repositoryRoot.FullName, settingsFile.RelativePath);
            SettingsFiles.Add(new SettingsFileViewModel(settingsFile, fullPath));
        }

        SelectedSettingsFile = SettingsFiles.FirstOrDefault();

        if (SelectedSettingsFile is not null)
            LoadSettingsFile(SelectedSettingsFile);
    }

    /// <summary>
    /// Обновляет весь Dashboard: Windows Service status и HTTP health.
    /// </summary>
    /// <summary>
    /// Загружает typed-представление appsettings из исходных файлов Runtime.Service и WEB.
    /// Published appsettings не читаем как источник правды: они являются результатом publish/deploy,
    /// но при сохранении обновляем и их, если службы уже опубликованы.
    /// </summary>
    private void LoadTypedAppSettings()
    {
        try
        {
            var runtime = ReadJsonObject(GetPreferredAppsettingsPath("runtime", @"TechMES.Runtime.Service\appsettings.json", "Runtime.Service"));
            var web = ReadJsonObject(GetPreferredAppsettingsPath("web", @"TechMES.Web\appsettings.json", "Web"));

            TypedAppSettings.RuntimeUrls = GetString(runtime, "Urls");
            TypedAppSettings.RuntimeDeviceName = GetString(runtime, "Runtime", "DeviceName");
            TypedAppSettings.RuntimeUserName = GetString(runtime, "Runtime", "UserName");
            TypedAppSettings.RuntimeEquipmentProvider = GetString(runtime, "EquipmentCatalog", "Provider");
            TypedAppSettings.RuntimeCtApiTableName = GetString(runtime, "EquipmentCatalog", "CtApiTableName");
            TypedAppSettings.RuntimeCtApiFilter = GetString(runtime, "EquipmentCatalog", "CtApiFilter");
            TypedAppSettings.RuntimeCtApiCluster = GetString(runtime, "EquipmentCatalog", "CtApiCluster");
            TypedAppSettings.RuntimeUseCtApiTypeFilter = GetBool(runtime, true, "EquipmentCatalog", "UseCtApiTypeFilter");

            TypedAppSettings.CtApiPath = GetString(runtime, "CtApi", "Path");
            TypedAppSettings.CtApiServer = GetString(runtime, "CtApi", "Server");
            TypedAppSettings.CtApiUser = GetString(runtime, "CtApi", "User");
            TypedAppSettings.CtApiPassword = GetString(runtime, "CtApi", "Password");
            TypedAppSettings.CtApiHealthCheckPeriodSeconds = GetInt(runtime, 10, "CtApi", "HealthCheckPeriodSeconds");
            TypedAppSettings.CtApiTagReadParallelism = GetInt(runtime, 4, "CtApi", "TagReadParallelism");
            TypedAppSettings.CtApiAllowWrites = GetBool(runtime, false, "CtApi", "AllowWrites");
            TypedAppSettings.CtApiHealthCheckTag = GetString(runtime, "CtApi", "HealthCheckTag");

            TypedAppSettings.RuntimeDatabaseConnectionString = GetString(runtime, "Database", "ConnectionString");
            TypedAppSettings.RuntimeEventDatabaseConnectionString = GetString(runtime, "EventDatabase", "ConnectionString");
            TypedAppSettings.ParamWritesEnabled = GetBool(runtime, false, "ParamWrites", "Enabled");
            TypedAppSettings.ParamWritesDryRun = GetBool(runtime, true, "ParamWrites", "DryRun");
            TypedAppSettings.ParamWritesRequireComment = GetBool(runtime, false, "ParamWrites", "RequireComment");
            TypedAppSettings.ParamWritesAuditEnabled = GetBool(runtime, true, "ParamWrites", "AuditEnabled");
            TypedAppSettings.ParamWritesAuthorizationEnabled = GetBool(runtime, false, "ParamWrites", "Authorization", "Enabled");
            TypedAppSettings.ParamWritesRequireWindowsUser = GetBool(runtime, true, "ParamWrites", "Authorization", "RequireWindowsUser");
            TypedAppSettings.ParamWritesAllowedUsers = GetString(runtime, "ParamWrites", "Authorization", "AllowedUsers");
            TypedAppSettings.ParamWritesAllowedGroups = GetString(runtime, "ParamWrites", "Authorization", "AllowedGroups");
            TypedAppSettings.RuntimeFileLoggingEnabled = GetBool(runtime, true, "FileLogging", "Enabled");
            TypedAppSettings.RuntimeFileLoggingMinimumLevel = GetString(runtime, "FileLogging", "MinimumLevel");
            TypedAppSettings.RuntimeFileLoggingDirectory = GetString(runtime, "FileLogging", "Directory");
            TypedAppSettings.RuntimeFileLoggingPrefix = GetString(runtime, "FileLogging", "FileNamePrefix");

            TypedAppSettings.WebUrls = GetString(web, "Urls");
            TypedAppSettings.WebRuntimeBaseUrl = GetString(web, "RuntimeService", "BaseUrl");
            TypedAppSettings.WebRuntimeTimeoutSeconds = GetInt(web, 30, "RuntimeService", "TimeoutSeconds");
            TypedAppSettings.WebMessagesHubPath = GetString(web, "RuntimeService", "MessagesHubPath");
            TypedAppSettings.WebDeviceName = GetString(web, "App", "DeviceName");
            TypedAppSettings.WebTitle = GetString(web, "App", "Title");
            TypedAppSettings.WebTrendWindowMinutes = GetInt(web, 30, "Param", "TrendWindowMinutes");
            TypedAppSettings.WebTrendHistoryMinutes = GetInt(web, 60, "Param", "TrendHistoryMinutes");
            TypedAppSettings.WebConfirmWrites = GetBool(web, false, "Param", "ConfirmWrites");
            TypedAppSettings.WebShowDeleteButton = GetBool(web, true, "Messages", "ShowDeleteButton");
            TypedAppSettings.WebHttpsRedirectionEnabled = GetBool(web, false, "HttpsRedirection", "Enabled");
            TypedAppSettings.WebWindowsAuthenticationEnabled = GetBool(web, false, "WindowsAuthentication", "Enabled");
            TypedAppSettings.WebFileLoggingEnabled = GetBool(web, true, "FileLogging", "Enabled");
            TypedAppSettings.WebFileLoggingMinimumLevel = GetString(web, "FileLogging", "MinimumLevel");
            TypedAppSettings.WebFileLoggingDirectory = GetString(web, "FileLogging", "Directory");
            TypedAppSettings.WebFileLoggingPrefix = GetString(web, "FileLogging", "FileNamePrefix");

            TypedAppSettings.Status = $"Runtime/Web appsettings loaded: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TypedAppSettings.Status = $"Runtime/Web appsettings load failed: {ex.Message}";
            AppendDiagnostics(TypedAppSettings.Status);
        }
    }

    /// <summary>
    /// Сохраняет typed-поля appsettings в один рабочий файл: published appsettings, если служба уже опубликована,
    /// иначе исходный appsettings проекта. Это не создает .bak рядом с файлом.
    /// После сохранения службам нужен restart, потому что appsettings читаются на старте процесса.
    /// </summary>
    private void SaveTypedRuntimeWebAppSettings()
    {
        NormalizeAuthenticationSettingsForSave();
        TypedAppSettings.ParamWritesDryRun = false;

        var updatedPaths = new List<string>();
        var skippedPaths = new List<string>();

        foreach (var path in GetWritableAppsettingsPaths("runtime", @"TechMES.Runtime.Service\appsettings.json", "Runtime.Service"))
        {
            if (!File.Exists(path))
            {
                skippedPaths.Add(path);
                continue;
            }

            var root = ReadJsonObject(path);
            ApplyRuntimeTypedSettings(root);
            SaveJson(path, root);
            updatedPaths.Add(path);
        }

        foreach (var path in GetWritableAppsettingsPaths("web", @"TechMES.Web\appsettings.json", "Web"))
        {
            if (!File.Exists(path))
            {
                skippedPaths.Add(path);
                continue;
            }

            var root = ReadJsonObject(path);
            ApplyWebTypedSettings(root);
            SaveJson(path, root);
            updatedPaths.Add(path);
        }

        TypedAppSettings.Status =
            $"Runtime/Web appsettings saved without backup: {updatedPaths.Count} file(s), skipped missing: {skippedPaths.Count}. Restart services to apply.";

        AppendDiagnostics(TypedAppSettings.Status);
        AppendServerLog(TypedAppSettings.Status);

        foreach (var settingsFile in SettingsFiles)
        {
            if (updatedPaths.Contains(settingsFile.FullPath, StringComparer.OrdinalIgnoreCase))
                LoadSettingsFile(settingsFile);
        }
    }

    /// <summary>
    /// Сводит упрощенную вкладку Authentication к полному набору внутренних настроек.
    /// В интерфейсе оператор видит только один список групп и основные флаги,
    /// а Maintenance/Runtime/Web получают согласованные значения без ручного дублирования.
    /// </summary>
    private void NormalizeAuthenticationSettingsForSave()
    {
        SecurityWindowsUsersEnabled = TypedAppSettings.WebWindowsAuthenticationEnabled;
        SecurityWriteGroups = TypedAppSettings.ParamWritesAllowedGroups;
        SecurityRequireWriteConfirmation = TypedAppSettings.WebConfirmWrites;
        SecurityRequireScadaAudit = TypedAppSettings.ParamWritesAuditEnabled;

        TypedAppSettings.ParamWritesRequireWindowsUser = TypedAppSettings.ParamWritesAuthorizationEnabled;
    }

    /// <summary>
    /// Переносит typed Runtime-поля в JSON-объект appsettings.
    /// Списки типов оборудования и aliases намеренно не трогаем: это более тонкая настройка каталога.
    /// </summary>
    private void ApplyRuntimeTypedSettings(JsonObject root)
    {
        SetValue(root, TypedAppSettings.RuntimeUrls, "Urls");
        SetValue(root, TypedAppSettings.RuntimeDeviceName, "Runtime", "DeviceName");
        SetValue(root, TypedAppSettings.RuntimeUserName, "Runtime", "UserName");
        SetValue(root, TypedAppSettings.RuntimeEquipmentProvider, "EquipmentCatalog", "Provider");
        SetValue(root, TypedAppSettings.RuntimeCtApiTableName, "EquipmentCatalog", "CtApiTableName");
        SetValue(root, TypedAppSettings.RuntimeCtApiFilter, "EquipmentCatalog", "CtApiFilter");
        SetValue(root, TypedAppSettings.RuntimeCtApiCluster, "EquipmentCatalog", "CtApiCluster");
        SetValue(root, TypedAppSettings.RuntimeUseCtApiTypeFilter, "EquipmentCatalog", "UseCtApiTypeFilter");

        SetValue(root, TypedAppSettings.CtApiPath, "CtApi", "Path");
        SetValue(root, TypedAppSettings.CtApiServer, "CtApi", "Server");
        SetValue(root, TypedAppSettings.CtApiUser, "CtApi", "User");
        SetValue(root, TypedAppSettings.CtApiPassword, "CtApi", "Password");
        SetValue(root, TypedAppSettings.CtApiHealthCheckPeriodSeconds, "CtApi", "HealthCheckPeriodSeconds");
        SetValue(root, TypedAppSettings.CtApiTagReadParallelism, "CtApi", "TagReadParallelism");
        SetValue(root, TypedAppSettings.CtApiAllowWrites, "CtApi", "AllowWrites");
        SetValue(root, TypedAppSettings.CtApiHealthCheckTag, "CtApi", "HealthCheckTag");

        SetValue(root, TypedAppSettings.RuntimeDatabaseConnectionString, "Database", "ConnectionString");
        SetValue(root, TypedAppSettings.RuntimeEventDatabaseConnectionString, "EventDatabase", "ConnectionString");
        SetValue(root, TypedAppSettings.ParamWritesEnabled, "ParamWrites", "Enabled");
        SetValue(root, false, "ParamWrites", "DryRun");
        SetValue(root, TypedAppSettings.ParamWritesRequireComment, "ParamWrites", "RequireComment");
        SetValue(root, TypedAppSettings.ParamWritesAuditEnabled, "ParamWrites", "AuditEnabled");
        SetValue(root, TypedAppSettings.ParamWritesAuthorizationEnabled, "ParamWrites", "Authorization", "Enabled");
        SetValue(root, TypedAppSettings.ParamWritesRequireWindowsUser, "ParamWrites", "Authorization", "RequireWindowsUser");
        SetValue(root, TypedAppSettings.ParamWritesAllowedUsers, "ParamWrites", "Authorization", "AllowedUsers");
        SetValue(root, TypedAppSettings.ParamWritesAllowedGroups, "ParamWrites", "Authorization", "AllowedGroups");
        SetValue(root, TypedAppSettings.RuntimeFileLoggingEnabled, "FileLogging", "Enabled");
        SetValue(root, TypedAppSettings.RuntimeFileLoggingMinimumLevel, "FileLogging", "MinimumLevel");
        SetValue(root, TypedAppSettings.RuntimeFileLoggingDirectory, "FileLogging", "Directory");
        SetValue(root, TypedAppSettings.RuntimeFileLoggingPrefix, "FileLogging", "FileNamePrefix");
    }

    /// <summary>
    /// Переносит typed WEB-поля в JSON-объект appsettings.
    /// Kestrel Certificate не трогаем здесь: им управляют Server-tab кнопки Prepare server / Apply HTTPS.
    /// </summary>
    private void ApplyWebTypedSettings(JsonObject root)
    {
        SetValue(root, TypedAppSettings.WebUrls, "Urls");
        SetValue(root, TypedAppSettings.WebRuntimeBaseUrl, "RuntimeService", "BaseUrl");
        SetValue(root, TypedAppSettings.WebRuntimeTimeoutSeconds, "RuntimeService", "TimeoutSeconds");
        SetValue(root, TypedAppSettings.WebMessagesHubPath, "RuntimeService", "MessagesHubPath");
        SetValue(root, TypedAppSettings.WebDeviceName, "App", "DeviceName");
        SetValue(root, TypedAppSettings.WebTitle, "App", "Title");
        SetValue(root, TypedAppSettings.WebTrendWindowMinutes, "Param", "TrendWindowMinutes");
        SetValue(root, TypedAppSettings.WebTrendHistoryMinutes, "Param", "TrendHistoryMinutes");
        SetValue(root, TypedAppSettings.WebConfirmWrites, "Param", "ConfirmWrites");
        SetValue(root, TypedAppSettings.WebShowDeleteButton, "Messages", "ShowDeleteButton");
        SetValue(root, TypedAppSettings.WebHttpsRedirectionEnabled, "HttpsRedirection", "Enabled");
        SetValue(root, TypedAppSettings.WebWindowsAuthenticationEnabled, "WindowsAuthentication", "Enabled");
        SetValue(root, TypedAppSettings.WebFileLoggingEnabled, "FileLogging", "Enabled");
        SetValue(root, TypedAppSettings.WebFileLoggingMinimumLevel, "FileLogging", "MinimumLevel");
        SetValue(root, TypedAppSettings.WebFileLoggingDirectory, "FileLogging", "Directory");
        SetValue(root, TypedAppSettings.WebFileLoggingPrefix, "FileLogging", "FileNamePrefix");
    }

    /// <summary>
    /// Возвращает исходный appsettings относительно корня репозитория.
    /// </summary>
    private string GetSourceAppsettingsPath(string relativePath) =>
        Path.Combine(_repositoryRoot.FullName, relativePath);

    /// <summary>
    /// Возвращает список рабочих appsettings для сохранения.
    /// Сейчас это один файл: published appsettings, если он есть, иначе исходный appsettings проекта.
    /// </summary>
    private IReadOnlyList<string> GetWritableAppsettingsPaths(
        string serviceKey,
        string sourceRelativePath,
        string fallbackPublishFolderName)
    {
        return [GetPreferredAppsettingsPath(serviceKey, sourceRelativePath, fallbackPublishFolderName)];
    }

    /// <summary>
    /// Возвращает рабочий appsettings: published-файл, если сервис уже опубликован, иначе исходный файл проекта.
    /// Так Settings меняет один понятный файл и не расходится между source и publish root.
    /// </summary>
    private string GetPreferredAppsettingsPath(
        string serviceKey,
        string sourceRelativePath,
        string fallbackPublishFolderName)
    {
        var service = GetServiceDefinition(serviceKey);
        var publishFolderName = string.IsNullOrWhiteSpace(service?.PublishFolderName)
            ? fallbackPublishFolderName
            : service.PublishFolderName!;

        var publishedPath = Path.Combine(
            _configuration.Deployment.PublishRoot,
            publishFolderName,
            "appsettings.json");

        return File.Exists(publishedPath)
            ? publishedPath
            : GetSourceAppsettingsPath(sourceRelativePath);
    }

    /// <summary>
    /// Читает JSON как объект верхнего уровня.
    /// </summary>
    private static JsonObject ReadJsonObject(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));

        return node as JsonObject
            ?? throw new InvalidOperationException($"Root JSON node is not an object: {path}");
    }

    /// <summary>
    /// Записывает форматированный JSON без соседнего .bak-файла.
    /// </summary>
    private static void SaveJson(string path, JsonObject root)
    {
        File.WriteAllText(path, root.ToJsonString(TypedAppSettingsJsonOptions));
    }

    /// <summary>
    /// Безопасно читает строковое значение из вложенного JSON-пути.
    /// </summary>
    private static string GetString(JsonObject root, params string[] path) =>
        TryGetValue<string>(root, path, out var value) ? value ?? "" : "";

    /// <summary>
    /// Безопасно читает integer из вложенного JSON-пути.
    /// </summary>
    private static int GetInt(JsonObject root, int defaultValue, params string[] path) =>
        TryGetValue<int>(root, path, out var value) ? value : defaultValue;

    /// <summary>
    /// Безопасно читает boolean из вложенного JSON-пути.
    /// </summary>
    private static bool GetBool(JsonObject root, bool defaultValue, params string[] path) =>
        TryGetValue<bool>(root, path, out var value) ? value : defaultValue;

    /// <summary>
    /// Пытается получить typed-значение из JSON без исключения наружу.
    /// </summary>
    private static bool TryGetValue<T>(
        JsonObject root,
        IReadOnlyList<string> path,
        out T? value)
    {
        value = default;
        JsonNode? current = root;

        foreach (var segment in path)
        {
            if (current is not JsonObject currentObject)
                return false;

            current = currentObject[segment];
        }

        if (current is null)
            return false;

        try
        {
            value = current.GetValue<T>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Записывает значение в JSON-путь, создавая недостающие промежуточные объекты.
    /// </summary>
    private static void SetValue(
        JsonObject root,
        object? value,
        params string[] path)
    {
        if (path.Length == 0)
            throw new ArgumentException("JSON path cannot be empty.", nameof(path));

        var parent = root;

        for (var index = 0; index < path.Length - 1; index++)
            parent = GetOrCreateObject(parent, path[index]);

        parent[path[^1]] = JsonValue.Create(value);
    }

    /// <summary>
    /// Возвращает вложенный объект или создает его, если раздел отсутствует.
    /// </summary>
    private static JsonObject GetOrCreateObject(
        JsonObject parent,
        string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private async Task RefreshAllAsync()
    {
        AppendDiagnostics("Refresh started.");

        foreach (var service in ServiceStatuses)
            await RefreshServiceAsync(service, includeHealth: true);

        AppendDiagnostics("Refresh finished.");
    }

    /// <summary>
    /// Запускает пакет диагностических проверок, которые нужны перед серверной эксплуатацией.
    /// Метод не изменяет настройки и службы: он только проверяет URL, TCP-доступность PostgreSQL и наличие ключевых файлов.
    /// </summary>
    private async Task RunDependencyChecksAsync()
    {
        DependencyChecks.Clear();
        DependencyCheckStatusText = "Checks running...";

        AppendDiagnostics("Dependency checks started.");

        foreach (var service in _configuration.Services)
            await AddHttpHealthCheckAsync(service);

        await AddPostgresTcpCheckAsync("Main PostgreSQL", TypedAppSettings.RuntimeDatabaseConnectionString);
        await AddPostgresTcpCheckAsync("Event PostgreSQL", TypedAppSettings.RuntimeEventDatabaseConnectionString);
        await AddPostgresAuthCheckAsync("Main PostgreSQL", TypedAppSettings.RuntimeDatabaseConnectionString);
        await AddPostgresAuthCheckAsync("Event PostgreSQL", TypedAppSettings.RuntimeEventDatabaseConnectionString);

        AddCtApiConfigurationChecks();
        AddFileSystemChecks();
        AddTargetMachineChecks();
        AddWriteSafetyChecks();

        var errors = DependencyChecks.Count(check => check.Status == "Error");
        var warnings = DependencyChecks.Count(check => check.Status == "Warning");
        var ok = DependencyChecks.Count(check => check.Status == "OK");

        DependencyCheckStatusText = $"Finished: OK {ok}, warnings {warnings}, errors {errors}.";
        AppendDiagnostics($"Dependency checks finished: OK {ok}, warnings {warnings}, errors {errors}.");
    }

    /// <summary>
    /// Проверяет один HTTP health URL и добавляет результат во вкладку Checks.
    /// </summary>
    private async Task AddHttpHealthCheckAsync(ServiceDefinition service)
    {
        var health = await _healthProbeService.ProbeAsync(service.HealthUrl);
        var status = health.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
            ? "OK"
            : health is "No URL" or "Timeout" or "TLS certificate error"
                ? "Warning"
                : "Error";

        AddDependencyCheck(
            "HTTP",
            service.DisplayName,
            status,
            $"{service.HealthUrl} -> {health}");
    }

    /// <summary>
    /// Проверяет, что PostgreSQL host/port из connection string доступны по TCP.
    /// Это легкая проверка сети; логин, пароль и схему данных по-прежнему проверяет Runtime.Service.
    /// </summary>
    private async Task AddPostgresTcpCheckAsync(
        string name,
        string connectionString)
    {
        if (!TryGetPostgresEndpoint(connectionString, out var host, out var port, out var database, out var reason))
        {
            AddDependencyCheck("PostgreSQL", name, "Warning", reason);
            return;
        }

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(3)));

            if (completedTask != connectTask)
            {
                AddDependencyCheck("PostgreSQL", name, "Warning", $"{host}:{port}, database '{database}': timeout.");
                return;
            }

            await connectTask;
            AddDependencyCheck("PostgreSQL", name, "OK", $"{host}:{port}, database '{database}': TCP connected.");
        }
        catch (Exception ex)
        {
            AddDependencyCheck("PostgreSQL", name, "Error", $"{host}:{port}, database '{database}': {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет PostgreSQL глубже TCP: логин, пароль, выбранную БД, текущего пользователя и версию сервера.
    /// Это безопасный read-only запрос, который помогает увидеть проблему авторизации до запуска Runtime.Service.
    /// </summary>
    private async Task AddPostgresAuthCheckAsync(
        string name,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            AddDependencyCheck("PostgreSQL auth", name, "Warning", "Connection string is empty.");
            return;
        }

        var result = await _postgreSqlProbeService.ProbeAsync(connectionString);
        if (!result.Success)
        {
            AddDependencyCheck("PostgreSQL auth", name, "Error", result.Error);
            return;
        }

        var details = $"DB '{result.Database}', user '{result.User}', {ShortenPostgreSqlVersion(result.Version)}.";
        AddDependencyCheck("PostgreSQL auth", name, "OK", details);
    }

    /// <summary>
    /// Проверяет DNS/hosts-алиас целевого сервера: имя должно резолвиться в ожидаемый IP или в один из IP текущего сервера.
    /// Это не проверка Windows machine name: короткое имя вроде techmes обычно задается в DNS или hosts.
    /// </summary>
    private HostResolutionStatus GetTargetHostResolutionStatus(IReadOnlyList<string> currentIps)
    {
        var hostName = _configuration.TargetMachine.HostName?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(hostName))
            return new HostResolutionStatus("Warning", "Host alias is empty.", []);

        try
        {
            var resolvedIps = Dns.GetHostAddresses(hostName)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (resolvedIps.Length == 0)
                return new HostResolutionStatus("Error", $"Host '{hostName}' resolved, but no IPv4 address was returned.", resolvedIps);

            var expectedIp = _configuration.TargetMachine.IpAddress?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(expectedIp))
            {
                var matchesExpectedIp = resolvedIps.Contains(expectedIp, StringComparer.OrdinalIgnoreCase);
                return new HostResolutionStatus(
                    matchesExpectedIp ? "OK" : "Error",
                    $"Host '{hostName}' -> {string.Join(", ", resolvedIps)}; expected IP: {expectedIp}.",
                    resolvedIps);
            }

            var matchesCurrentMachine = resolvedIps.Any(ip => currentIps.Contains(ip, StringComparer.OrdinalIgnoreCase));
            return new HostResolutionStatus(
                matchesCurrentMachine ? "OK" : "Warning",
                $"Host '{hostName}' -> {string.Join(", ", resolvedIps)}; current server IPs: {string.Join(", ", currentIps)}.",
                resolvedIps);
        }
        catch (Exception ex)
        {
            return new HostResolutionStatus("Error", $"Host '{hostName}' cannot be resolved by Windows DNS/hosts: {ex.Message}", []);
        }
    }

    /// <summary>
    /// Проверяет, что Maintenance запущен на ожидаемой машине и видит ожидаемый IP.
    /// Пустые поля профиля считаются предупреждением: сервер работает, но профиль еще не заполнен.
    /// </summary>
    private void AddTargetMachineChecks()
    {
        AddDependencyCheck(
            "Target machine",
            "Display name",
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.DisplayName) ? "Warning" : "OK",
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.DisplayName)
                ? "Target machine display name is empty."
                : _configuration.TargetMachine.DisplayName);

        var expectedHost = _configuration.TargetMachine.HostName;
        var currentIps = ServerAddresses.Select(address => address.Address).ToList();
        var hostResolution = GetTargetHostResolutionStatus(currentIps);

        AddDependencyCheck(
            "Target machine",
            "Host alias",
            hostResolution.Status,
            string.IsNullOrWhiteSpace(expectedHost)
                ? $"Host alias is empty. Windows machine name: {Environment.MachineName}."
                : hostResolution.Details);

        var expectedIp = _configuration.TargetMachine.IpAddress;
        AddDependencyCheck(
            "Target machine",
            "IPv4 address",
            string.IsNullOrWhiteSpace(expectedIp)
                ? "Warning"
                : currentIps.Contains(expectedIp, StringComparer.OrdinalIgnoreCase) ? "OK" : "Error",
            string.IsNullOrWhiteSpace(expectedIp)
                ? $"Expected IP is empty. Current IPs: {string.Join(", ", currentIps)}."
                : $"Expected: {expectedIp}; current IPs: {string.Join(", ", currentIps)}.");
    }

    /// <summary>
    /// Проверяет Windows-безопасность write-режима и ее соответствие Runtime/Web appsettings.
    /// Runtime выполняет allow-list enforcement, а WEB отвечает за получение Windows identity через Negotiate.
    /// </summary>
    private void AddWriteSafetyChecks()
    {
        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        AddDependencyCheck(
            "Write safety",
            "Current Windows user",
            "OK",
            currentUser);

        AddDependencyCheck(
            "Write safety",
            "Windows users profile",
            _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            _configuration.Security.WindowsUsersEnabled
                ? "Windows user profile is enabled."
                : "Windows user profile is disabled.");

        AddDependencyCheck(
            "Write safety",
            "Runtime authorization",
            TypedAppSettings.ParamWritesAuthorizationEnabled == _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            $"Profile: {_configuration.Security.WindowsUsersEnabled}; Runtime appsettings: {TypedAppSettings.ParamWritesAuthorizationEnabled}.");

        AddDependencyCheck(
            "Write safety",
            "WEB Windows auth",
            TypedAppSettings.WebWindowsAuthenticationEnabled == _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            $"Profile: {_configuration.Security.WindowsUsersEnabled}; WEB appsettings: {TypedAppSettings.WebWindowsAuthenticationEnabled}.");

        AddDependencyCheck(
            "Write safety",
            "Write groups",
            string.IsNullOrWhiteSpace(_configuration.Security.WriteGroups) ? "Warning" : "OK",
            string.IsNullOrWhiteSpace(_configuration.Security.WriteGroups)
                ? "Write groups are empty."
                : _configuration.Security.WriteGroups);

        AddDependencyCheck(
            "Write safety",
            "Runtime allowed groups",
            string.Equals(TypedAppSettings.ParamWritesAllowedGroups, _configuration.Security.WriteGroups, StringComparison.OrdinalIgnoreCase)
                ? "OK"
                : "Warning",
            $"Profile: {_configuration.Security.WriteGroups}; Runtime appsettings: {TypedAppSettings.ParamWritesAllowedGroups}.");

        AddDependencyCheck(
            "Write safety",
            "Runtime writes",
            TypedAppSettings.ParamWritesEnabled && TypedAppSettings.CtApiAllowWrites ? "OK" : "Warning",
            $"Enabled: {TypedAppSettings.ParamWritesEnabled}; CtApi writes: {TypedAppSettings.CtApiAllowWrites}.");

        AddDependencyCheck(
            "Write safety",
            "Write confirmation",
            TypedAppSettings.WebConfirmWrites == _configuration.Security.RequireWriteConfirmation ? "OK" : "Warning",
            $"Profile: {_configuration.Security.RequireWriteConfirmation}; WEB appsettings: {TypedAppSettings.WebConfirmWrites}.");

        AddDependencyCheck(
            "Write safety",
            "SCADA audit",
            !_configuration.Security.RequireScadaAudit || TypedAppSettings.ParamWritesAuditEnabled ? "OK" : "Warning",
            $"Profile requires SCADA audit: {_configuration.Security.RequireScadaAudit}; Runtime audit enabled: {TypedAppSettings.ParamWritesAuditEnabled}.");
    }

    private void RefreshWindowsAuthDiagnostics()
    {
        WindowsAuthDiagnostics.Clear();

        var currentUser = GetCurrentWindowsUser();
        var currentGroups = GetCurrentWindowsGroups();
        var configuredUsers = SplitPrincipalList(TypedAppSettings.ParamWritesAllowedUsers);
        var configuredGroups = SplitPrincipalList(TypedAppSettings.ParamWritesAllowedGroups);
        var profileGroups = SplitPrincipalList(_configuration.Security.WriteGroups);

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Windows auth",
            "Current Maintenance user",
            "OK",
            currentUser);

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Windows auth",
            "Current Windows groups",
            currentGroups.Count == 0 ? "Warning" : "OK",
            FormatPrincipalList(currentGroups, 18));

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "WEB",
            "Windows Authentication",
            TypedAppSettings.WebWindowsAuthenticationEnabled == _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            $"Maintenance profile: {_configuration.Security.WindowsUsersEnabled}; WEB appsettings: {TypedAppSettings.WebWindowsAuthenticationEnabled}.");

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Runtime",
            "Write authorization",
            TypedAppSettings.ParamWritesAuthorizationEnabled == _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            $"Maintenance profile: {_configuration.Security.WindowsUsersEnabled}; Runtime appsettings: {TypedAppSettings.ParamWritesAuthorizationEnabled}; require Windows user: {TypedAppSettings.ParamWritesRequireWindowsUser}.");

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Allow-list",
            "Profile write groups",
            profileGroups.Count == 0 ? "Warning" : "OK",
            profileGroups.Count == 0 ? "No write groups are configured in maintenance.settings.json." : string.Join("; ", profileGroups));

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Allow-list",
            "Runtime allowed principals",
            configuredUsers.Count == 0 && configuredGroups.Count == 0 ? "Error" : "OK",
            $"Users: {FormatPrincipalList(configuredUsers, 10)}; groups: {FormatPrincipalList(configuredGroups, 10)}.");

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Allow-list",
            "Profile vs Runtime groups",
            PrincipalListsMatch(profileGroups, configuredGroups) ? "OK" : "Warning",
            $"Profile: {FormatPrincipalList(profileGroups, 10)}; Runtime: {FormatPrincipalList(configuredGroups, 10)}.");

        var currentUserAllowed = IsPrincipalAllowed(currentUser, currentGroups, configuredUsers, configuredGroups);
        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Allow-list",
            "Current user simulation",
            !TypedAppSettings.ParamWritesAuthorizationEnabled ? "Warning" : currentUserAllowed ? "OK" : "Warning",
            !TypedAppSettings.ParamWritesAuthorizationEnabled
                ? "Runtime allow-list enforcement is disabled, so the current user is not checked."
                : currentUserAllowed
                    ? "The current Windows user or one of its groups matches Runtime allow-list."
                    : "The current Maintenance user does not match Runtime allow-list. This may be normal if WEB is tested from another Windows account.");

        AddDiagnosticsRow(
            WindowsAuthDiagnostics,
            "Browser",
            "Integrated auth hints",
            TypedAppSettings.WebWindowsAuthenticationEnabled ? "OK" : "Warning",
            "Chrome/Edge can pass Windows credentials for intranet/trusted hosts. If a browser prompts for credentials, configure the host as trusted or use the browser AuthServerAllowlist policy.");

        UpdateDiagnosticsStatus(WindowsAuthDiagnostics, value => WindowsAuthDiagnosticsStatusText = value);
    }

    private void RefreshOperatorActionDiagnostics()
    {
        OperatorActionDiagnostics.Clear();

        var eventDbConfigured = !string.IsNullOrWhiteSpace(TypedAppSettings.RuntimeEventDatabaseConnectionString);
        var eventDbEndpoint = TryGetPostgresEndpoint(
            TypedAppSettings.RuntimeEventDatabaseConnectionString,
            out var eventHost,
            out var eventPort,
            out var eventDatabase,
            out var eventReason)
                ? $"{eventHost}:{eventPort}/{eventDatabase}"
                : eventReason;

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Write flow",
            "Param writes",
            TypedAppSettings.ParamWritesEnabled ? "OK" : "Warning",
            $"Enabled: {TypedAppSettings.ParamWritesEnabled}; WEB confirmation: {TypedAppSettings.WebConfirmWrites}.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Write flow",
            "CtApi writes",
            TypedAppSettings.CtApiAllowWrites ? "OK" : "Warning",
            $"CtApi AllowWrites: {TypedAppSettings.CtApiAllowWrites}; CtApi server: {TypedAppSettings.CtApiServer}; user: {TypedAppSettings.CtApiUser}.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Audit",
            "SCADA audit switch",
            TypedAppSettings.ParamWritesAuditEnabled ? "OK" : _configuration.Security.RequireScadaAudit ? "Error" : "Warning",
            $"Maintenance profile requires audit: {_configuration.Security.RequireScadaAudit}; Runtime ParamWrites:AuditEnabled: {TypedAppSettings.ParamWritesAuditEnabled}.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Audit",
            "SaveActionOperators",
            TypedAppSettings.ParamWritesAuditEnabled ? "OK" : "Warning",
            "Runtime write-flow should call Cicode SaveActionOperators(name, current, new, description, deviceName) after successful real writes.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Audit",
            "Runtime device name",
            string.IsNullOrWhiteSpace(TypedAppSettings.RuntimeDeviceName) ? "Warning" : "OK",
            string.IsNullOrWhiteSpace(TypedAppSettings.RuntimeDeviceName)
                ? "Runtime device name is empty; operator actions will be harder to identify."
                : TypedAppSettings.RuntimeDeviceName);

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Event DB",
            "Connection string",
            eventDbConfigured ? "OK" : "Error",
            eventDbConfigured ? eventDbEndpoint : "Runtime EventDatabase:ConnectionString is empty.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Operation actions",
            "Runtime endpoint",
            eventDbConfigured ? "OK" : "Error",
            eventDbConfigured
                ? "Runtime exposes /api/event-log/operator-actions and reads existing EventPicker/PostgreSQL tables."
                : "Operation actions cannot be loaded without Event DB connection string.");

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Operation actions",
            "WEB page",
            eventDbConfigured ? "OK" : "Warning",
            "WEB page Operation actions reads Runtime API. No duplicate audit tables are created by WEB.");

        var ready = TypedAppSettings.ParamWritesEnabled
            && TypedAppSettings.CtApiAllowWrites
            && TypedAppSettings.ParamWritesAuditEnabled
            && eventDbConfigured;

        AddDiagnosticsRow(
            OperatorActionDiagnostics,
            "Readiness",
            "Operator action chain",
            ready ? "OK" : "Warning",
            ready
                ? "Real writes, CtApi writes, SCADA audit and Event DB are configured."
                : "One or more parts are not ready: Param writes, CtApi writes, SCADA audit or Event DB.");

        UpdateDiagnosticsStatus(OperatorActionDiagnostics, value => OperatorActionDiagnosticsStatusText = value);
    }

    /// <summary>
    /// Проверяет обязательные поля CtApi.
    /// Сам вызов Citect из Maintenance пока не выполняем, чтобы не добавлять второй SCADA-клиент рядом с Runtime.Service.
    /// </summary>
    private void AddCtApiConfigurationChecks()
    {
        AddPathCheck("CtApi", "CtApi path", TypedAppSettings.CtApiPath, pathCanBeFile: true);
        AddRequiredTextCheck("CtApi", "CtApi server", TypedAppSettings.CtApiServer);
        AddRequiredTextCheck("CtApi", "CtApi user", TypedAppSettings.CtApiUser);
        AddRequiredTextCheck("CtApi", "Health check tag", TypedAppSettings.CtApiHealthCheckTag);
    }

    /// <summary>
    /// Проверяет папки публикации, appsettings, сертификаты и log-директории.
    /// Эти пути чаще всего ломаются при переносе проекта на сервер или после ручного изменения настроек.
    /// </summary>
    private void AddFileSystemChecks()
    {
        AddDirectoryCheck("Files", "Repository root", _repositoryRoot.FullName);
        AddDirectoryCheck("Files", "Publish root", _configuration.Deployment.PublishRoot);
        AddDirectoryCheck("Files", "Published Runtime.Service", Path.Combine(_configuration.Deployment.PublishRoot, "Runtime.Service"));
        AddDirectoryCheck("Files", "Published WEB", Path.Combine(_configuration.Deployment.PublishRoot, "Web"));

        AddFileCheck("Files", "Source Runtime appsettings", GetSourceAppsettingsPath(@"TechMES.Runtime.Service\appsettings.json"));
        AddFileCheck("Files", "Source WEB appsettings", GetSourceAppsettingsPath(@"TechMES.Web\appsettings.json"));
        AddFileCheck("Files", "Published Runtime appsettings", Path.Combine(_configuration.Deployment.PublishRoot, "Runtime.Service", "appsettings.json"));
        AddFileCheck("Files", "Published WEB appsettings", Path.Combine(_configuration.Deployment.PublishRoot, "Web", "appsettings.json"));

        AddFileCheck("Files", "HTTPS PFX", ServerCertificatePfxPath);
        AddFileCheck("Files", "Public CER", ServerPublicCertificatePath);

        AddDirectoryCheck("Logs", "Runtime logs", Path.Combine(_configuration.Deployment.PublishRoot, "Runtime.Service", "logs"));
        AddDirectoryCheck("Logs", "WEB logs", Path.Combine(_configuration.Deployment.PublishRoot, "Web", "logs"));
    }

    /// <summary>
    /// Добавляет проверку обязательного текстового параметра.
    /// </summary>
    private void AddRequiredTextCheck(
        string category,
        string name,
        string value)
    {
        AddDependencyCheck(
            category,
            name,
            string.IsNullOrWhiteSpace(value) ? "Warning" : "OK",
            string.IsNullOrWhiteSpace(value) ? "Value is empty." : value);
    }

    /// <summary>
    /// Проверяет наличие файла.
    /// </summary>
    private void AddFileCheck(
        string category,
        string name,
        string path)
    {
        AddDependencyCheck(
            category,
            name,
            File.Exists(path) ? "OK" : "Warning",
            path);
    }

    /// <summary>
    /// Проверяет наличие папки.
    /// </summary>
    private void AddDirectoryCheck(
        string category,
        string name,
        string path)
    {
        AddDependencyCheck(
            category,
            name,
            Directory.Exists(path) ? "OK" : "Warning",
            path);
    }

    /// <summary>
    /// Проверяет путь, который может быть как папкой, так и файлом.
    /// </summary>
    private void AddPathCheck(
        string category,
        string name,
        string path,
        bool pathCanBeFile)
    {
        var exists = pathCanBeFile
            ? File.Exists(path) || Directory.Exists(path)
            : Directory.Exists(path);

        AddDependencyCheck(
            category,
            name,
            exists ? "OK" : "Warning",
            string.IsNullOrWhiteSpace(path) ? "Path is empty." : path);
    }

    /// <summary>
    /// Добавляет одну строку результата во вкладку Checks.
    /// </summary>
    private void AddDependencyCheck(
        string category,
        string name,
        string status,
        string details)
    {
        AddDiagnosticsRow(DependencyChecks, category, name, status, details);
    }

    private static void AddDiagnosticsRow(
        ObservableCollection<DependencyCheckViewModel> target,
        string category,
        string name,
        string status,
        string details)
    {
        target.Add(new DependencyCheckViewModel
        {
            Category = category,
            Name = name,
            Status = status,
            Details = details,
            CheckedAt = DateTime.Now
        });
    }

    private static void UpdateDiagnosticsStatus(
        ObservableCollection<DependencyCheckViewModel> rows,
        Action<string> applyStatus)
    {
        var errors = rows.Count(check => check.Status == "Error");
        var warnings = rows.Count(check => check.Status == "Warning");
        var ok = rows.Count(check => check.Status == "OK");

        applyStatus($"Checked: OK {ok}, warnings {warnings}, errors {errors}.");
    }

    private static string GetCurrentWindowsUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!string.IsNullOrWhiteSpace(identity.Name))
                return identity.Name;
        }
        catch
        {
            // Environment fallback is enough for a read-only diagnostic row.
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    private static List<string> GetCurrentWindowsGroups()
    {
        var result = new List<string>();

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.Groups is null)
                return result;

            foreach (var group in identity.Groups)
            {
                try
                {
                    result.Add(group.Translate(typeof(NTAccount)).Value);
                }
                catch
                {
                    result.Add(group.Value);
                }
            }
        }
        catch
        {
            return result;
        }

        return result
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SplitPrincipalList(string? value)
    {
        return (value ?? "")
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrincipalAllowed(
        string currentUser,
        IReadOnlyCollection<string> currentGroups,
        IReadOnlyCollection<string> allowedUsers,
        IReadOnlyCollection<string> allowedGroups)
    {
        return allowedUsers.Any(allowed => PrincipalMatches(currentUser, allowed))
            || currentGroups.Any(group => allowedGroups.Any(allowed => PrincipalMatches(group, allowed)));
    }

    private static bool PrincipalListsMatch(
        IReadOnlyCollection<string> first,
        IReadOnlyCollection<string> second)
    {
        return first.Count == second.Count
            && first.All(item => second.Any(other => PrincipalMatches(item, other)));
    }

    private static bool PrincipalMatches(string actual, string allowed)
    {
        if (string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase))
            return true;

        if (allowed.Contains('\\', StringComparison.Ordinal))
            return false;

        return actual.EndsWith("\\" + allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPrincipalList(IReadOnlyCollection<string> items, int maxItems)
    {
        if (items.Count == 0)
            return "-";

        var visible = items.Take(maxItems).ToList();
        var suffix = items.Count > visible.Count ? $" ... (+{items.Count - visible.Count})" : "";

        return string.Join("; ", visible) + suffix;
    }

    /// <summary>
    /// Достает host, port и database из PostgreSQL connection string.
    /// Поддерживаются стандартные ключи Npgsql: Host, Server, Port, Database.
    /// </summary>
    private static bool TryGetPostgresEndpoint(
        string connectionString,
        out string host,
        out int port,
        out string database,
        out string reason)
    {
        host = "";
        port = 5432;
        database = "";
        reason = "";

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            reason = "Connection string is empty.";
            return false;
        }

        var values = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.OrdinalIgnoreCase);

        host = GetConnectionStringValue(values, "Host", "Server", "Data Source", "Address", "Addr", "Network Address");
        database = GetConnectionStringValue(values, "Database", "Initial Catalog");

        var portText = GetConnectionStringValue(values, "Port");
        if (!string.IsNullOrWhiteSpace(portText) && !int.TryParse(portText, out port))
        {
            reason = $"Invalid PostgreSQL port: {portText}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            reason = "Connection string does not contain Host/Server.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(database))
            database = "-";

        return true;
    }

    /// <summary>
    /// Возвращает первое найденное значение из connection string по списку возможных ключей.
    /// </summary>
    private static string GetConnectionStringValue(
        Dictionary<string, string> values,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
                return value;
        }

        return "";
    }

    /// <summary>
    /// Возвращает короткую строку версии PostgreSQL без длинного хвоста компилятора и ОС.
    /// </summary>
    private static string ShortenPostgreSqlVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "version is empty";

        var separatorIndex = version.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        return separatorIndex > 0
            ? version[..separatorIndex]
            : version;
    }

    /// <summary>
    /// Обновляет сетевые адреса, локальные TCP-порты и состояние firewall-правила.
    /// Это быстрая проверка серверного режима без изменения настроек Windows.
    /// </summary>
    private async Task RefreshServerAsync()
    {
        RefreshServerAddressRows();

        var webListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.WebPort);
        var httpsListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.HttpsPort);
        var runtimeListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.RuntimePort);

        _serverWebPortState = webListening ? "Listening" : "Not listening";
        _serverHttpsPortState = httpsListening ? "Listening" : "Not listening";
        _serverRuntimePortState = runtimeListening ? "Listening" : "Not listening";
        OnPropertyChanged(nameof(ServerWebPortLine));
        OnPropertyChanged(nameof(ServerHttpsPortLine));
        OnPropertyChanged(nameof(ServerRuntimePortLine));

        ServerPortStatus =
            $"WEB {ServerWebPortLine}; " +
            $"HTTPS {ServerHttpsPortLine}; " +
            $"Runtime {ServerRuntimePortLine}";

        var firewall = await _firewallManager.QueryInboundTcpRuleAsync(
            _configuration.Server.FirewallRuleName,
            _configuration.Server.WebPort);

        var httpsFirewall = await _firewallManager.QueryInboundTcpRuleAsync(
            _configuration.Server.HttpsFirewallRuleName,
            _configuration.Server.HttpsPort);

        var certificate = _httpsCertificateManager.GetCertificateInfo(_configuration.Server);

        ServerFirewallStatus = firewall.Status;
        ServerHttpsFirewallStatus = httpsFirewall.Status;
        ServerCertificateStatus = certificate.Status;

        AppendServerLog(
            $"Refresh: {ServerPortStatus}. " +
            $"HTTP firewall: {ServerFirewallStatus}. " +
            $"HTTPS firewall: {ServerHttpsFirewallStatus}. " +
            $"Certificate: {ServerCertificateStatus}.");

        RefreshServerProfile();
    }

    /// <summary>
    /// Собирает понятный профиль сервера из текущих настроек и последних живых статусов.
    /// Профиль не хранится отдельно, чтобы не было второго источника правды рядом с maintenance.settings.json.
    /// </summary>
    private void RefreshServerProfile()
    {
        ServerProfileItems.Clear();

        var webListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.WebPort);
        var httpsListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.HttpsPort);
        var runtimeListening = _serverNetworkService.IsTcpPortListening(_configuration.Server.RuntimePort);
        var certificate = _httpsCertificateManager.GetCertificateInfo(_configuration.Server);
        var ipAddresses = ServerAddresses
            .Select(address => address.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddServerProfileItem("Network", "Host name", Environment.MachineName, "OK", "Windows machine name.");
        AddServerProfileItem(
            "Network",
            "IP addresses",
            ipAddresses.Count == 0 ? "-" : string.Join(", ", ipAddresses),
            ipAddresses.Count == 0 ? "Warning" : "OK",
            ipAddresses.Count == 0 ? "No IPv4 address found for tablet access." : "Use one of these addresses from tablet/clients.");

        AddServerProfileItem(
            "Target",
            "Display name",
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.DisplayName) ? "-" : _configuration.TargetMachine.DisplayName,
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.DisplayName) ? "Warning" : "OK",
            "Operator-friendly target server name.");

        var hostResolution = GetTargetHostResolutionStatus(ipAddresses);

        AddServerProfileItem(
            "Target",
            "Host alias",
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.HostName) ? "-" : _configuration.TargetMachine.HostName,
            hostResolution.Status,
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.HostName)
                ? $"Host alias is empty. Windows machine name: {Environment.MachineName}."
                : hostResolution.Details);

        AddServerProfileItem(
            "Target",
            "Expected IP",
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.IpAddress) ? "-" : _configuration.TargetMachine.IpAddress,
            string.IsNullOrWhiteSpace(_configuration.TargetMachine.IpAddress)
                ? "Warning"
                : ipAddresses.Contains(_configuration.TargetMachine.IpAddress, StringComparer.OrdinalIgnoreCase) ? "OK" : "Error",
            $"Current IPs: {(ipAddresses.Count == 0 ? "-" : string.Join(", ", ipAddresses))}.");

        AddServerProfileItem(
            "Ports",
            "HTTP",
            _configuration.Server.WebPort.ToString(),
            webListening ? "OK" : "Error",
            webListening ? "Listening." : "Not listening.");

        AddServerProfileItem(
            "Ports",
            "HTTPS",
            _configuration.Server.HttpsPort.ToString(),
            httpsListening ? "OK" : "Error",
            httpsListening ? "Listening." : "Not listening.");

        AddServerProfileItem(
            "Ports",
            "Runtime",
            _configuration.Server.RuntimePort.ToString(),
            runtimeListening ? "OK" : "Error",
            runtimeListening ? "Listening on local runtime port." : "Runtime port is not listening.");

        AddServerProfileItem(
            "Certificate",
            "HTTPS certificate",
            certificate.Status,
            certificate.HasPfx && certificate.HasPublicCertificate ? "OK" : "Warning",
            $"PFX: {certificate.PfxPath}; CER: {certificate.PublicCertificatePath}");

        AddServerProfileItem(
            "Firewall",
            "HTTP rule",
            _configuration.Server.FirewallRuleName,
            ServerFirewallStatus == "Open" ? "OK" : "Warning",
            string.IsNullOrWhiteSpace(ServerFirewallStatus) ? "Not checked yet." : ServerFirewallStatus);

        AddServerProfileItem(
            "Firewall",
            "HTTPS rule",
            _configuration.Server.HttpsFirewallRuleName,
            ServerHttpsFirewallStatus == "Open" ? "OK" : "Warning",
            string.IsNullOrWhiteSpace(ServerHttpsFirewallStatus) ? "Not checked yet." : ServerHttpsFirewallStatus);

        AddServerProfileItem(
            "Deploy",
            "Publish path",
            _configuration.Deployment.PublishRoot,
            Directory.Exists(_configuration.Deployment.PublishRoot) ? "OK" : "Error",
            Directory.Exists(_configuration.Deployment.PublishRoot) ? "Directory exists." : "Publish directory does not exist.");

        foreach (var disk in _cleanupService.GetDiskStatuses(_configuration, BackupRoot))
        {
            AddServerProfileItem(
                "Storage",
                disk.Name,
                disk.FreeText,
                disk.Status,
                $"Drive: {disk.Drive}; total: {disk.TotalText}; path: {disk.Path}");
        }

        AddServerProfileItem(
            "Security",
            "Windows users",
            _configuration.Security.WindowsUsersEnabled ? "Enabled" : "Disabled",
            _configuration.Security.WindowsUsersEnabled ? "OK" : "Warning",
            $"Current user: {Environment.UserDomainName}\\{Environment.UserName}.");

        AddServerProfileItem(
            "Security",
            "Write groups",
            string.IsNullOrWhiteSpace(_configuration.Security.WriteGroups) ? "-" : _configuration.Security.WriteGroups,
            string.IsNullOrWhiteSpace(_configuration.Security.WriteGroups) ? "Warning" : "OK",
            "Future WEB/Runtime write-mode authorization groups.");

        AddServerProfileItem(
            "Security",
            "Write confirmation",
            _configuration.Security.RequireWriteConfirmation ? "Required" : "Not required",
            "OK",
            "Checks tab compares profile with WEB appsettings.");

        AddServerProfileItem(
            "Security",
            "SCADA audit",
            _configuration.Security.RequireScadaAudit ? "Required" : "Not required",
            "OK",
            "Runtime write flow should call SaveActionOperators when enabled.");

        foreach (var service in ServiceStatuses)
        {
            AddServerProfileItem(
                "Services",
                service.DisplayName,
                service.ServiceName,
                IsServiceRunning(service) && IsHealthOk(service) ? "OK" : "Warning",
                $"Status: {service.Status}; health: {service.Health}; URL: {service.HealthUrl}");
        }

        UpdateServerReadiness(
            webListening,
            httpsListening,
            runtimeListening,
            certificate.HasPfx && certificate.HasPublicCertificate);

        ServerProfileLastUpdated = $"Updated: {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// Добавляет одну строку в табличную часть профиля сервера.
    /// </summary>
    private void AddServerProfileItem(
        string section,
        string name,
        string value,
        string status,
        string details)
    {
        ServerProfileItems.Add(new ServerProfileItemViewModel
        {
            Section = section,
            Name = name,
            Value = value,
            Status = status,
            Details = details
        });
    }

    /// <summary>
    /// Пересчитывает итоговую готовность сервера и собирает список причин, если сервер еще не готов.
    /// </summary>
    private void UpdateServerReadiness(
        bool webListening,
        bool httpsListening,
        bool runtimeListening,
        bool certificateReady)
    {
        var issues = new List<string>();

        if (!webListening)
            issues.Add($"HTTP port {_configuration.Server.WebPort} is not listening");

        if (!httpsListening)
            issues.Add($"HTTPS port {_configuration.Server.HttpsPort} is not listening");

        if (!runtimeListening)
            issues.Add($"Runtime port {_configuration.Server.RuntimePort} is not listening");

        if (!certificateReady)
            issues.Add("HTTPS certificate files are not ready");

        if (ServerFirewallStatus != "Open")
            issues.Add("HTTP firewall rule is not open or not checked");

        if (ServerHttpsFirewallStatus != "Open")
            issues.Add("HTTPS firewall rule is not open or not checked");

        if (!Directory.Exists(_configuration.Deployment.PublishRoot))
            issues.Add("Publish root does not exist");

        var currentIps = ServerAddresses.Select(address => address.Address).ToList();
        var hostResolution = GetTargetHostResolutionStatus(currentIps);
        if (hostResolution.Status == "Error")
        {
            issues.Add(hostResolution.Details);
        }

        if (!string.IsNullOrWhiteSpace(_configuration.TargetMachine.IpAddress) &&
            !currentIps.Contains(_configuration.TargetMachine.IpAddress, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add($"Expected IP {_configuration.TargetMachine.IpAddress} was not found on this machine");
        }

        foreach (var service in ServiceStatuses)
        {
            if (!IsServiceRunning(service))
                issues.Add($"{service.DisplayName} is {service.Status}");

            if (!IsHealthOk(service))
                issues.Add($"{service.DisplayName} health is {service.Health}");
        }

        if (issues.Count == 0)
        {
            ServerProfileReadinessStatus = "Ready";
            ServerProfileReadinessDetails = "Server profile is ready for WEB clients.";
            return;
        }

        ServerProfileReadinessStatus = "Not ready";
        ServerProfileReadinessDetails = string.Join("; ", issues);
    }

    /// <summary>
    /// Возвращает true, если Windows Service сейчас запущен.
    /// </summary>
    private static bool IsServiceRunning(ServiceStatusViewModel service) =>
        string.Equals(service.Status, "RUNNING", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Возвращает true, если health endpoint вернул успешный HTTP-ответ.
    /// </summary>
    private static bool IsHealthOk(ServiceStatusViewModel service) =>
        service.Health.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Перечитывает IPv4-адреса локальной машины и формирует URL для планшета.
    /// </summary>
    private void RefreshServerAddressRows()
    {
        ServerAddresses.Clear();

        foreach (var address in _serverNetworkService.GetWebAddresses(
            _configuration.Server.WebPort,
            _configuration.Server.HttpsPort))
        {
            ServerAddresses.Add(address);
        }
    }

    /// <summary>
    /// Обновляет одну строку сервиса.
    /// </summary>
    private async Task RefreshServiceAsync(
        ServiceStatusViewModel service,
        bool includeHealth)
    {
        service.IsBusy = true;

        try
        {
            var status = await _serviceManager.QueryAsync(service.ServiceName);
            service.Status = status.Status;
            service.Details = status.Details;
            service.LastChecked = DateTime.Now;

            if (includeHealth)
                service.Health = await _healthProbeService.ProbeAsync(service.HealthUrl);

            AppendDiagnostics($"{service.DisplayName}: {service.Status}, health {service.Health}");
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Обработчик кнопки общего обновления.
    /// </summary>
    private async void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Обновляет только вкладку Server.
    /// </summary>
    /// <summary>
    /// Запускает ручную preflight-проверку окружения.
    /// </summary>
    private async void OnRunDependencyChecksClick(object sender, RoutedEventArgs e)
    {
        await RunDependencyChecksAsync();
    }

    /// <summary>
    /// Обновляет статусы служб, серверные проверки и после этого пересчитывает итоговый профиль сервера.
    /// </summary>
    private async void OnRefreshServerProfileClick(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        await RefreshServerAsync();
    }

    private void OnRefreshWindowsAuthDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        RefreshWindowsAuthDiagnostics();
    }

    private void OnRefreshOperatorActionDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        RefreshOperatorActionDiagnostics();
    }

    private async void OnRefreshServerClick(object sender, RoutedEventArgs e)
    {
        await RefreshServerAsync();
    }

    /// <summary>
    /// Opens a Windows management console from Maintenance and writes the result to diagnostics.
    /// </summary>
    private void OpenWindowsManagementTool(string fileName, string displayName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName)
            {
                UseShellExecute = true
            });
            AppendDiagnostics($"{displayName}: opened.");
        }
        catch (Exception ex)
        {
            AppendDiagnostics($"{displayName}: open failed: {ex.Message}");
            MessageBox.Show(
                this,
                $"Не удалось открыть {displayName}.\n\n{ex.Message}",
                "TechMES Maintenance",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnOpenCertificateManagerClick(object sender, RoutedEventArgs e)
    {
        OpenWindowsManagementTool("certmgr.msc", "Certificate Manager");
    }

    private void OnOpenFirewallAdvancedClick(object sender, RoutedEventArgs e)
    {
        OpenWindowsManagementTool("wf.msc", "Windows Defender Firewall with Advanced Security");
    }

    private void OnOpenLocalUsersAndGroupsClick(object sender, RoutedEventArgs e)
    {
        OpenWindowsManagementTool("lusrmgr.msc", "Local Users and Groups");
    }

    /// <summary>
    /// Создает или обновляет входящее firewall-правило для WEB-порта.
    /// Для успешного выполнения Maintenance должен быть запущен от имени администратора.
    /// </summary>
    private async void OnOpenWebFirewallClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            $"Maintenance создаст или обновит входящее правило Windows Firewall '{_configuration.Server.FirewallRuleName}' для TCP-порта {_configuration.Server.WebPort}.\n\nПродолжить?",
            "Open WEB firewall",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            AppendServerLog("WEB firewall update cancelled by user.");
            return;
        }

        await EnsureWebFirewallAsync();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Создает новый self-signed сертификат для WEB HTTPS.
    /// После генерации PFX подключается в Kestrel, а CER можно импортировать на планшет как доверенный.
    /// </summary>
    private async void OnCreateHttpsCertificateClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Maintenance создаст или заменит локальный HTTPS-сертификат WEB, обновит PFX/CER файлы и затем перечитает серверный статус.\n\nПродолжить?",
            "Create/replace HTTPS certificate",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            AppendServerLog("Certificate create/replace cancelled by user.");
            return;
        }

        CreateHttpsCertificate();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Прописывает HTTPS endpoint в appsettings WEB.
    /// Чтобы изменения вступили в силу, после этого нужно перезапустить TechMES.Web.
    /// </summary>
    private async void OnApplyHttpsConfigClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            $"Maintenance пропишет в WEB appsettings HTTPS endpoint на порту {_configuration.Server.HttpsPort} и путь к PFX-сертификату:\n{ServerCertificatePfxPath}\n\nПосле применения нужно перезапустить TechMES WEB.\n\nПродолжить?",
            "Apply HTTPS",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            AppendServerLog("WEB HTTPS configuration update cancelled by user.");
            return;
        }

        ApplyWebHttpsConfiguration();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Создает или обновляет firewall-правило для HTTPS-порта WEB.
    /// </summary>
    private async void OnOpenHttpsFirewallClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            $"Maintenance создаст или обновит входящее правило Windows Firewall '{_configuration.Server.HttpsFirewallRuleName}' для TCP-порта {_configuration.Server.HttpsPort}.\n\nПродолжить?",
            "Open HTTPS firewall",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            AppendServerLog("HTTPS firewall update cancelled by user.");
            return;
        }

        await EnsureHttpsFirewallAsync();
        await RefreshServerAsync();
    }

    /// <summary>
    /// Полная подготовка серверной машины одной кнопкой.
    /// Сценарий намеренно вызывает те же helper-методы, что и отдельные кнопки Server/Deploy:
    /// так ручной и автоматический режимы не расходятся по настройкам firewall, HTTPS и Windows Services.
    /// </summary>
    private async void OnPrepareServerClick(object sender, RoutedEventArgs e)
    {
        AppendServerLog("Prepare server started.");
        AppendDeploymentLog("Prepare server requested from Server tab.");

        try
        {
            _configurationStore.Save(_configuration);
            AppendServerLog("Maintenance configuration saved.");
        }
        catch (Exception ex)
        {
            AppendServerLog($"Maintenance configuration save failed: {ex.Message}");
        }

        EnsureHttpsCertificate();
        ApplyWebHttpsConfiguration();
        await EnsureWebFirewallAsync();
        await EnsureHttpsFirewallAsync();
        await DeployAllServicesAsync();

        await RefreshAllAsync();
        await RefreshServerAsync();

        AppendServerLog("Prepare server finished.");
        AppendDeploymentLog("Prepare server finished.");
    }

    /// <summary>
    /// Проверяет, есть ли уже пригодный PFX/CER комплект, и не пересоздает его без необходимости.
    /// Это важно для планшетов: если каждый Prepare server генерирует новый self-signed сертификат,
    /// ранее импортированный CER перестает совпадать с сертификатом Kestrel, и Chrome снова показывает Not secure.
    /// Принудительная замена остается на отдельной кнопке Create/replace cert.
    /// </summary>
    private void EnsureHttpsCertificate()
    {
        var certificate = _httpsCertificateManager.GetCertificateInfo(_configuration.Server);

        if (certificate.HasPfx
            && certificate.HasPublicCertificate
            && certificate.NotAfter > DateTimeOffset.Now.AddDays(30)
            && !string.Equals(certificate.Thumbprint, "Cannot read PFX", StringComparison.OrdinalIgnoreCase))
        {
            ServerCertificateStatus = certificate.Status;
            AppendServerLog($"Certificate reused: {certificate.Status}");
            AppendServerLog($"CER: {certificate.PublicCertificatePath}");
            return;
        }

        AppendServerLog("Certificate is missing, unreadable or close to expiration. Creating a new one...");
        CreateHttpsCertificate();
    }

    /// <summary>
    /// Создает или обновляет входящее firewall-правило для HTTP WEB-порта.
    /// Helper используется и отдельной кнопкой, и общим сценарием Prepare server.
    /// </summary>
    private async Task EnsureWebFirewallAsync()
    {
        AppendServerLog($"Opening firewall rule '{_configuration.Server.FirewallRuleName}' for TCP {_configuration.Server.WebPort}.");

        var result = await _firewallManager.EnsureInboundTcpRuleAsync(
            _configuration.Server.FirewallRuleName,
            _configuration.Server.WebPort);

        ServerFirewallStatus = result.Status;
        AppendServerLog($"Firewall result: {result.Status}.");

        if (!string.IsNullOrWhiteSpace(result.Details))
            AppendServerLog(result.Details);
    }

    /// <summary>
    /// Создает или обновляет входящее firewall-правило для HTTPS WEB-порта.
    /// </summary>
    private async Task EnsureHttpsFirewallAsync()
    {
        AppendServerLog($"Opening firewall rule '{_configuration.Server.HttpsFirewallRuleName}' for TCP {_configuration.Server.HttpsPort}.");

        var result = await _firewallManager.EnsureInboundTcpRuleAsync(
            _configuration.Server.HttpsFirewallRuleName,
            _configuration.Server.HttpsPort);

        ServerHttpsFirewallStatus = result.Status;
        AppendServerLog($"HTTPS firewall result: {result.Status}.");

        if (!string.IsNullOrWhiteSpace(result.Details))
            AppendServerLog(result.Details);
    }

    /// <summary>
    /// Генерирует PFX/CER комплект для Kestrel и клиентского доверия.
    /// Метод синхронный, потому что создание сертификата работает с локальным файловым хранилищем.
    /// </summary>
    private void CreateHttpsCertificate()
    {
        try
        {
            AppendServerLog("Creating HTTPS certificate...");
            var certificate = _httpsCertificateManager.CreateOrReplaceCertificate(
                _configuration.Server,
                _configuration.TargetMachine.HostName);

            ServerCertificateStatus = certificate.Status;
            AppendServerLog($"Certificate: {certificate.Status}");
            AppendServerLog($"PFX: {certificate.PfxPath}");
            AppendServerLog($"CER: {certificate.PublicCertificatePath}");

            if (!string.IsNullOrWhiteSpace(certificate.Thumbprint))
                AppendServerLog($"Thumbprint: {certificate.Thumbprint}");
        }
        catch (Exception ex)
        {
            ServerCertificateStatus = "Create failed";
            AppendServerLog($"Certificate create failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Прописывает HTTP/HTTPS endpoints WEB в исходный и опубликованный appsettings.json.
    /// Если опубликованного WEB еще нет, source-appsettings все равно будет подготовлен для следующего publish.
    /// </summary>
    private void ApplyWebHttpsConfiguration()
    {
        try
        {
            AppendServerLog("Applying WEB HTTPS configuration...");
            var result = _webHttpsConfigurator.ApplyHttpsConfiguration(_configuration);

            AppendServerLog(result.Status);

            if (!string.IsNullOrWhiteSpace(result.Details))
                AppendServerLog(result.Details);
        }
        catch (Exception ex)
        {
            AppendServerLog($"HTTPS configuration failed: {ex.Message}");
        }
    }

    private void OnSaveDeploymentProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _configurationStore.Save(_configuration);
            AppendDeploymentLog("Deployment profile saved.");
        }
        catch (Exception ex)
        {
            AppendDeploymentLog($"Deployment profile save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Сохраняет typed-настройки Maintenance: deploy-профиль, server-порты, сертификат и health URLs.
    /// Raw JSON editor остается ниже как аварийный ручной режим, но основные поля теперь можно менять формой.
    /// </summary>
    private void OnSaveTypedSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            NormalizeAuthenticationSettingsForSave();
            _configurationStore.Save(_configuration);
            RefreshDeploymentPaths();
            RefreshServerAddressRows();
            RefreshDiskStatuses();
            RefreshServerProfile();
            RefreshWindowsAuthDiagnostics();
            RefreshOperatorActionDiagnostics();

            foreach (var service in ServiceStatuses)
                service.RefreshDefinitionBindings();

            foreach (var service in DeploymentServices)
                service.RefreshDefinitionBindings();

            AppendDiagnostics("Typed Maintenance settings saved.");
            AppendServerLog("Typed Maintenance settings saved.");
            AppendDeploymentLog("Typed Maintenance settings saved.");
        }
        catch (Exception ex)
        {
            AppendDiagnostics($"Typed Maintenance settings save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Публикует все сервисы в Deployment:PublishRoot.
    /// </summary>
    /// <summary>
    /// Перечитывает typed Runtime/Web appsettings с диска.
    /// Используется, если raw JSON был изменен вручную или файлы обновились после publish/deploy.
    /// </summary>
    private void OnReloadTypedRuntimeWebAppSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadTypedAppSettings();
        RefreshWindowsAuthDiagnostics();
        RefreshOperatorActionDiagnostics();
        AppendDiagnostics(TypedAppSettings.Status);
    }

    /// <summary>
    /// Сохраняет typed Runtime/Web appsettings.
    /// Это отдельная кнопка от Save typed settings, потому что меняет не maintenance.settings.json,
    /// а реальные appsettings служб.
    /// </summary>
    private void OnSaveTypedRuntimeWebAppSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveTypedRuntimeWebAppSettings();
            RefreshWindowsAuthDiagnostics();
            RefreshOperatorActionDiagnostics();
        }
        catch (Exception ex)
        {
            TypedAppSettings.Status = $"Runtime/Web appsettings save failed: {ex.Message}";
            AppendDiagnostics(TypedAppSettings.Status);
        }
    }

    /// <summary>
    /// Перечитывает typed-настройки Runtime/Web appsettings для новой страницы Settings.
    /// Maintenance-профиль остается в текущем экземпляре окна: его поля уже редактируются напрямую через binding.
    /// </summary>
    private void OnReloadAllTypedSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadTypedAppSettings();
        RefreshWindowsAuthDiagnostics();
        RefreshOperatorActionDiagnostics();
        AppendDiagnostics(TypedAppSettings.Status);
    }

    /// <summary>
    /// Сохраняет все typed-настройки из Settings: Maintenance profile и Runtime/Web appsettings.
    /// Это общая кнопка Save под вкладками Settings, чтобы оператору не приходилось помнить разные кнопки сохранения.
    /// </summary>
    private void OnSaveAllTypedSettingsClick(object sender, RoutedEventArgs e)
    {
        OnSaveTypedSettingsClick(sender, e);
        OnSaveTypedRuntimeWebAppSettingsClick(sender, e);
    }

    private async void OnPublishAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var service in DeploymentServices)
            await PublishDeploymentServiceAsync(service);
    }

    /// <summary>
    /// Устанавливает или обновляет все Windows Services.
    /// </summary>
    private async void OnInstallAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var service in DeploymentServices)
            await InstallDeploymentServiceAsync(service);
    }

    /// <summary>
    /// Запускает все сервисы из deploy-таблицы.
    /// </summary>
    private async void OnStartAllDeployClick(object sender, RoutedEventArgs e)
    {
        foreach (var service in DeploymentServices)
            await StartDeploymentServiceAsync(service);
    }

    /// <summary>
    /// Полный сценарий одной кнопкой: остановить, опубликовать, установить/обновить и запустить.
    /// Stop выполняется best-effort, потому что при первой установке службы еще может не быть.
    /// </summary>
    private async void OnDeployAllClick(object sender, RoutedEventArgs e)
    {
        await DeployAllServicesAsync();
        await RefreshAllAsync();
    }

    /// <summary>
    /// Общий deploy-сценарий для всех сервисов: остановить, опубликовать, установить/обновить и запустить.
    /// Метод используется вкладкой Deploy и полной подготовкой сервера, чтобы не дублировать порядок операций.
    /// </summary>
    private async Task DeployAllServicesAsync()
    {
        AppendDeploymentLog("Deploy all started.");

        foreach (var service in DeploymentServices)
        {
            await StopDeploymentServiceAsync(service);

            var published = await PublishDeploymentServiceAsync(service);
            if (!published)
                continue;

            var installed = await InstallDeploymentServiceAsync(service);
            if (!installed)
                continue;

            await StartDeploymentServiceAsync(service);
        }

        AppendDeploymentLog("Deploy all finished.");
    }

    /// <summary>
    /// Публикует сервис из строки DataGrid.
    /// </summary>
    private async void OnPublishDeployServiceClick(object sender, RoutedEventArgs e)
    {
        if (TryGetDeploymentServiceFromButton(sender, out var service))
            await PublishDeploymentServiceAsync(service);
    }

    /// <summary>
    /// Устанавливает или обновляет Windows Service из строки DataGrid.
    /// </summary>
    private async void OnInstallDeployServiceClick(object sender, RoutedEventArgs e)
    {
        if (TryGetDeploymentServiceFromButton(sender, out var service))
            await InstallDeploymentServiceAsync(service);
    }

    /// <summary>
    /// Выполняет dotnet publish для одного сервиса.
    /// </summary>
    private async Task<bool> PublishDeploymentServiceAsync(DeploymentServiceViewModel service)
    {
        service.IsBusy = true;
        service.Status = "Publishing...";
        AppendDeploymentLog($"{service.DisplayName}: publish started.");

        try
        {
            var result = await _deploymentManager.PublishAsync(service.Definition, _configuration.Deployment);
            var success = result.ExitCode == 0;

            service.Status = success ? "Published" : "Publish failed";
            RefreshDeploymentPaths();
            AppendDeploymentLog($"{service.DisplayName}: publish {(success ? "OK" : "FAILED")}");

            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                AppendDeploymentLog(result.CombinedOutput);

            return success;
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Устанавливает или обновляет один Windows Service.
    /// </summary>
    private async Task<bool> InstallDeploymentServiceAsync(DeploymentServiceViewModel service)
    {
        service.IsBusy = true;
        service.Status = "Installing...";
        AppendDeploymentLog($"{service.DisplayName}: install/update started.");

        try
        {
            var result = await _deploymentManager.InstallOrUpdateServiceAsync(service.Definition, _configuration.Deployment);
            service.Status = result.Success ? $"Installed ({result.Status})" : result.Status;
            AppendDeploymentLog($"{service.DisplayName}: install/update {(result.Success ? "OK" : "FAILED")} - {result.Status}");

            if (!string.IsNullOrWhiteSpace(result.Details))
                AppendDeploymentLog(result.Details);

            return result.Success;
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Best-effort остановка сервиса перед публикацией.
    /// </summary>
    private async Task StopDeploymentServiceAsync(DeploymentServiceViewModel service)
    {
        AppendDeploymentLog($"{service.DisplayName}: stop requested.");
        var result = await _serviceManager.StopAsync(service.ServiceName);
        AppendDeploymentLog($"{service.DisplayName}: stop result {result.Status}");
    }

    /// <summary>
    /// Запускает сервис после установки или обновления.
    /// </summary>
    private async Task StartDeploymentServiceAsync(DeploymentServiceViewModel service)
    {
        service.IsBusy = true;
        service.Status = "Starting...";
        AppendDeploymentLog($"{service.DisplayName}: start requested.");

        try
        {
            var result = await _serviceManager.StartAsync(service.ServiceName);
            service.Status = result.Success ? $"Started ({result.Status})" : result.Status;
            AppendDeploymentLog($"{service.DisplayName}: start {(result.Success ? "OK" : "FAILED")} - {result.Status}");
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Запускает выбранный Windows Service.
    /// </summary>
    private async void OnStartServiceClick(object sender, RoutedEventArgs e)
    {
        if (TryGetServiceFromButton(sender, out var service))
            await RunServiceCommandAsync(service, () => _serviceManager.StartAsync(service.ServiceName));
    }

    /// <summary>
    /// Останавливает выбранный Windows Service.
    /// </summary>
    private async void OnStopServiceClick(object sender, RoutedEventArgs e)
    {
        if (TryGetServiceFromButton(sender, out var service))
            await RunServiceCommandAsync(service, () => _serviceManager.StopAsync(service.ServiceName));
    }

    /// <summary>
    /// Перезапускает выбранный Windows Service.
    /// </summary>
    private async void OnRestartServiceClick(object sender, RoutedEventArgs e)
    {
        if (TryGetServiceFromButton(sender, out var service))
            await RunServiceCommandAsync(service, () => _serviceManager.RestartAsync(service.ServiceName));
    }

    /// <summary>
    /// Выполняет HTTP health-check выбранного сервиса.
    /// </summary>
    private async void OnProbeServiceClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetServiceFromButton(sender, out var service))
            return;

        service.IsBusy = true;

        try
        {
            service.Health = await _healthProbeService.ProbeAsync(service.HealthUrl);
            service.LastChecked = DateTime.Now;
            AppendDiagnostics($"{service.DisplayName}: health {service.Health}");
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Выполняет start/stop/restart и затем перечитывает статус строки.
    /// </summary>
    private async Task RunServiceCommandAsync(
        ServiceStatusViewModel service,
        Func<Task<ServiceCommandResult>> command)
    {
        service.IsBusy = true;

        try
        {
            var result = await command();
            service.Status = result.Status;
            service.Details = result.Details;
            service.LastChecked = DateTime.Now;
            AppendDiagnostics($"{service.DisplayName}: command result {result.Status}");
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    /// <summary>
    /// Достает ServiceStatusViewModel из кнопки внутри DataGrid.
    /// </summary>
    private static bool TryGetServiceFromButton(
        object sender,
        out ServiceStatusViewModel service)
    {
        if (sender is Button { Tag: ServiceStatusViewModel viewModel })
        {
            service = viewModel;
            return true;
        }

        service = null!;
        return false;
    }

    /// <summary>
    /// Достает DeploymentServiceViewModel из кнопки внутри deploy-таблицы.
    /// </summary>
    private static bool TryGetDeploymentServiceFromButton(
        object sender,
        out DeploymentServiceViewModel service)
    {
        if (sender is Button { Tag: DeploymentServiceViewModel viewModel })
        {
            service = viewModel;
            return true;
        }

        service = null!;
        return false;
    }

    /// <summary>
    /// Загружает выбранный appsettings-файл при смене выбора в списке.
    /// </summary>
    private void OnSettingsFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedSettingsFile is not null && string.IsNullOrEmpty(SelectedSettingsFile.Content))
            LoadSettingsFile(SelectedSettingsFile);
    }

    /// <summary>
    /// Перечитывает выбранный appsettings с диска.
    /// </summary>
    private void OnReloadSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSettingsFile is not null)
            LoadSettingsFile(SelectedSettingsFile);
    }

    /// <summary>
    /// Сохраняет выбранный appsettings напрямую, без соседнего .bak-файла.
    /// </summary>
    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSettingsFile is null)
            return;

        try
        {
            _settingsFileService.SaveWithoutBackup(
                SelectedSettingsFile.FullPath,
                SelectedSettingsFile.Content);

            SelectedSettingsFile.IsDirty = false;
            SelectedSettingsFile.Status = "Saved.";
            AppendDiagnostics($"{SelectedSettingsFile.DisplayName}: saved.");
        }
        catch (Exception ex)
        {
            SelectedSettingsFile.Status = $"Save failed: {ex.Message}";
            AppendDiagnostics($"{SelectedSettingsFile.DisplayName}: save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Читает файл настроек и показывает статус загрузки.
    /// </summary>
    private void LoadSettingsFile(SettingsFileViewModel settingsFile)
    {
        try
        {
            var content = _settingsFileService.Load(settingsFile.FullPath);
            settingsFile.SetCleanContent(content);
            settingsFile.Status = File.Exists(settingsFile.FullPath)
                ? $"Loaded: {DateTime.Now:HH:mm:ss}"
                : "File not found.";
        }
        catch (Exception ex)
        {
            settingsFile.Status = $"Load failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Перечитывает список log-файлов.
    /// </summary>
    private void OnRefreshLogsClick(object sender, RoutedEventArgs e)
    {
        RefreshLogRows();
    }

    /// <summary>
    /// Применяет выбранную дату просмотра логов.
    /// DatePicker меняет только фильтр списка, сами log-файлы не изменяются.
    /// </summary>
    private void OnLogDateSelected(object sender, SelectionChangedEventArgs e)
    {
        RefreshLogRows();
    }

    /// <summary>
    /// Переключает журнал на предыдущий день и сразу обновляет список найденных log-файлов.
    /// </summary>
    private void OnPreviousLogDateClick(object sender, RoutedEventArgs e)
    {
        LogSelectedDate = (LogSelectedDate ?? DateTime.Today).Date.AddDays(-1);
        RefreshLogRows();
    }

    /// <summary>
    /// Возвращает фильтр журналов на сегодняшний день.
    /// </summary>
    private void OnTodayLogDateClick(object sender, RoutedEventArgs e)
    {
        LogSelectedDate = DateTime.Today;
        RefreshLogRows();
    }

    /// <summary>
    /// Переключает журнал на следующий день и сразу обновляет список найденных log-файлов.
    /// </summary>
    private void OnNextLogDateClick(object sender, RoutedEventArgs e)
    {
        LogSelectedDate = (LogSelectedDate ?? DateTime.Today).Date.AddDays(1);
        RefreshLogRows();
    }

    /// <summary>
    /// Читает хвост выбранного log-файла.
    /// </summary>
    private void OnLogFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedLogFile is null)
        {
            SelectedLogText = "";
            return;
        }

        try
        {
            SelectedLogText = _logFileService.ReadTail(SelectedLogFile.FullPath);
        }
        catch (Exception ex)
        {
            SelectedLogText = ex.Message;
        }
    }

    /// <summary>
    /// Заполняет список логов найденными файлами.
    /// </summary>
    private void RefreshLogRows()
    {
        LogFiles.Clear();

        foreach (var logFile in _logFileService.FindLogs(
            _configuration.LogSearchRoots,
            _configuration.Deployment.PublishRoot,
            LogSelectedDate))
        {
            LogFiles.Add(logFile);
        }

        SelectedLogFile = LogFiles.FirstOrDefault();

        if (SelectedLogFile is not null)
        {
            SelectedLogText = _logFileService.ReadTail(SelectedLogFile.FullPath);
        }
        else
        {
            SelectedLogText =
                "No log files found yet." + Environment.NewLine +
                $"Selected date: {(LogSelectedDate is null ? "all dates" : LogSelectedDate.Value.ToString("dd.MM.yyyy"))}" + Environment.NewLine +
                "Expected locations:" + Environment.NewLine +
                $"- {_configuration.Deployment.PublishRoot}\\Runtime.Service\\logs" + Environment.NewLine +
                $"- {_configuration.Deployment.PublishRoot}\\Web\\logs" + Environment.NewLine +
                "- repository _runlogs / TechMES.Runtime.Service / TechMES.Web" + Environment.NewLine +
                "After the next service start, TechMES.Web and Runtime.Service will create daily .log files.";
        }
    }

    /// <summary>
    /// Перечитывает список backup-снимков из выбранной папки.
    /// Метод не меняет файлы на диске, он только обновляет таблицу во вкладке Backup / Restore.
    /// </summary>
    private void RefreshBackupRows()
    {
        BackupItems.Clear();

        foreach (var backup in _backupRestoreService.FindBackups(BackupRoot))
            BackupItems.Add(backup);

        SelectedBackup = BackupItems.FirstOrDefault();
        BackupStatusText = BackupItems.Count == 0
            ? "No backups found yet."
            : $"Loaded {BackupItems.Count} backup(s).";
    }

    /// <summary>
    /// Сохраняет выбранный backup root в maintenance.settings.json.
    /// </summary>
    private void PersistBackupRoot()
    {
        if (string.IsNullOrWhiteSpace(BackupRoot))
            BackupRoot = GetDefaultBackupRoot();

        _configuration.Cleanup.BackupRoot = BackupRoot;
        _configurationStore.Save(_configuration);
        RefreshDiskStatuses();
    }

    /// <summary>
    /// Запоминает путь backup после редактирования поля во вкладке Backup / Restore.
    /// </summary>
    private void OnBackupRootLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistBackupRoot();
            RefreshBackupRows();
            BackupStatusText = $"Backup root saved: {BackupRoot}";
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Backup root save failed: {ex.Message}";
            AppendDiagnostics(BackupStatusText);
        }
    }

    /// <summary>
    /// Открывает стандартный системный выбор папки для корня Backup / Restore.
    /// </summary>
    private void OnBrowseBackupRootClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select TechMES backup folder",
            InitialDirectory = Directory.Exists(BackupRoot) ? BackupRoot : GetDefaultBackupRoot()
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            BackupRoot = dialog.FolderName;
            PersistBackupRoot();
            RefreshBackupRows();
            BackupStatusText = $"Backup root saved: {BackupRoot}";
            AppendDiagnostics(BackupStatusText);
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Backup root save failed: {ex.Message}";
            AppendDiagnostics(BackupStatusText);
        }
    }

    /// <summary>
    /// Обновляет таблицу свободного места для дисков, где лежат репозиторий, публикация и backup.
    /// Это отдельная быстрая операция: она не сканирует файлы и ничего не удаляет.
    /// </summary>
    private void RefreshDiskStatuses()
    {
        DiskStatuses.Clear();

        foreach (var status in _cleanupService.GetDiskStatuses(_configuration, BackupRoot))
            DiskStatuses.Add(status);
    }

    /// <summary>
    /// Сканирует безопасные кандидаты на очистку: старые .log, side-backup appsettings и старые backup-папки.
    /// Сканирование не удаляет файлы, а только показывает оператору список.
    /// </summary>
    private void OnScanCleanupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanupItems.Clear();
            RefreshDiskStatuses();

            foreach (var item in _cleanupService.Scan(_configuration, BackupRoot))
                CleanupItems.Add(item);

            CleanupStatusText = CleanupItems.Count == 0
                ? "No cleanup candidates found."
                : $"Found {CleanupItems.Count} cleanup candidate(s). Review the list before delete.";

            AppendDiagnostics($"Cleanup scan finished: {CleanupItems.Count} candidate(s).");
        }
        catch (Exception ex)
        {
            CleanupStatusText = $"Cleanup scan failed: {ex.Message}";
            AppendDiagnostics(CleanupStatusText);
        }
    }

    /// <summary>
    /// Удаляет текущий список кандидатов. Автоматической очистки нет:
    /// оператор сначала сканирует, смотрит таблицу, затем вручную подтверждает кнопкой Delete.
    /// </summary>
    private void OnDeleteCleanupCandidatesClick(object sender, RoutedEventArgs e)
    {
        if (CleanupItems.Count == 0)
        {
            CleanupStatusText = "Scan cleanup candidates first.";
            return;
        }

        try
        {
            var items = CleanupItems.ToList();
            CleanupStatusText = _cleanupService.Delete(items);
            CleanupItems.Clear();
            RefreshDiskStatuses();
            RefreshLogRows();
            RefreshBackupRows();
            AppendDiagnostics($"Cleanup delete finished: {CleanupStatusText}");
        }
        catch (Exception ex)
        {
            CleanupStatusText = $"Cleanup delete failed: {ex.Message}";
            AppendDiagnostics(CleanupStatusText);
        }
    }

    /// <summary>
    /// Сохраняет профиль целевой машины, security-настройки и retention-политику.
    /// Это та же maintenance.settings.json, но кнопка вынесена рядом с эксплуатационными действиями.
    /// </summary>
    private void OnSaveMaintenanceProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _configurationStore.Save(_configuration);
            RefreshDiskStatuses();
            RefreshServerProfile();
            CleanupStatusText = "Maintenance profile saved.";
            AppendDiagnostics("Maintenance profile saved.");
        }
        catch (Exception ex)
        {
            CleanupStatusText = $"Maintenance profile save failed: {ex.Message}";
            AppendDiagnostics(CleanupStatusText);
        }
    }

    /// <summary>
    /// Создает новый backup-снимок конфигурации и сразу обновляет список.
    /// </summary>
    private void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistBackupRoot();
            var backup = _backupRestoreService.CreateBackup(_configuration, BackupRoot);
            RefreshBackupRows();
            SelectedBackup = BackupItems.FirstOrDefault(item => item.FullPath == backup.FullPath) ?? BackupItems.FirstOrDefault();
            BackupStatusText = $"Backup created: {backup.DisplayName}.";
            AppendDiagnostics($"Backup created: {backup.FullPath}");
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Backup failed: {ex.Message}";
            AppendDiagnostics($"Backup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Обновляет таблицу backup-снимков по кнопке Refresh backups.
    /// </summary>
    private void OnRefreshBackupsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistBackupRoot();
            RefreshBackupRows();
            AppendDiagnostics("Backup list refreshed.");
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Refresh failed: {ex.Message}";
            AppendDiagnostics($"Backup refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Восстанавливает выбранный backup. Сервис перед перезаписью делает .restore-backup копии текущих файлов.
    /// После восстановления перечитываем appsettings и profile, но службы оператор перезапускает отдельно.
    /// </summary>
    private void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBackup is null)
        {
            BackupStatusText = "Select backup first.";
            return;
        }

        try
        {
            PersistBackupRoot();
            BackupStatusText = _backupRestoreService.RestoreBackup(SelectedBackup.FullPath);
            LoadTypedAppSettings();
            RefreshServerAddressRows();
            RefreshServerProfile();
            RefreshLogRows();
            AppendDiagnostics($"Backup restored: {SelectedBackup.FullPath}");
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Restore failed: {ex.Message}";
            AppendDiagnostics($"Backup restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Экспортирует выбранный backup-снимок в zip-архив рядом с backup root.
    /// </summary>
    private void OnExportBackupZipClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBackup is null)
        {
            BackupStatusText = "Select backup first.";
            return;
        }

        try
        {
            PersistBackupRoot();
            var zipPath = _backupRestoreService.ExportBackupZip(SelectedBackup.FullPath);
            BackupStatusText = $"Backup exported: {zipPath}";
            AppendDiagnostics($"Backup exported to zip: {zipPath}");
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Export failed: {ex.Message}";
            AppendDiagnostics($"Backup export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Импортирует backup-снимок из zip-архива в текущий backup root.
    /// </summary>
    private void OnImportBackupZipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import TechMES backup zip",
            Filter = "ZIP backup (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            PersistBackupRoot();
            var imported = _backupRestoreService.ImportBackupZip(dialog.FileName, BackupRoot);
            RefreshBackupRows();
            SelectedBackup = BackupItems.FirstOrDefault(item => item.FullPath == imported.FullPath) ?? BackupItems.FirstOrDefault();
            BackupStatusText = $"Backup imported: {imported.DisplayName}";
            AppendDiagnostics($"Backup imported from zip: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Import failed: {ex.Message}";
            AppendDiagnostics($"Backup import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Открывает папку TechMES.Maintenance в проводнике.
    /// </summary>
    private void OnOpenMaintenanceFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(_repositoryRoot.FullName, "TechMES.Maintenance");

        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder)
            {
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Добавляет строку в диагностическое окно Dashboard.
    /// </summary>
    private void AppendDiagnostics(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DiagnosticsText = string.IsNullOrWhiteSpace(DiagnosticsText)
            ? line
            : DiagnosticsText + Environment.NewLine + line;
    }

    /// <summary>
    /// Добавляет строку в журнал вкладки Deploy.
    /// </summary>
    private void AppendDeploymentLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DeploymentLogText = string.IsNullOrWhiteSpace(DeploymentLogText)
            ? line
            : DeploymentLogText + Environment.NewLine + line;
    }

    /// <summary>
    /// Добавляет строку в журнал вкладки Server.
    /// </summary>
    private void AppendServerLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        ServerLogText = string.IsNullOrWhiteSpace(ServerLogText)
            ? line
            : ServerLogText + Environment.NewLine + line;
    }

    /// <summary>
    /// Уведомляет WPF binding об изменении свойства окна.
    /// </summary>
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
