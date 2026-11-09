using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>Хранит профили БД и готовит автономный сценарий из единственного штатного инструмента.</summary>
public sealed class PostgreSqlSetupService(string? storageDirectory = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "PostgreSqlSetup");

    private string Storage => storageDirectory ?? Root;
    private string ProfilesPath => Path.Combine(Storage, "profiles.json");

    /// <summary>Загружает профили; повреждённый файл не подменяется молча настройками по умолчанию.</summary>
    public List<PostgreSqlSetupProfile> LoadProfiles()
    {
        var profiles = File.Exists(ProfilesPath)
            ? JsonSerializer.Deserialize<List<PostgreSqlSetupProfile>>(File.ReadAllText(ProfilesPath), Json) ?? throw new InvalidDataException("Пустой файл профилей PostgreSQL.")
            : [new(), new() { Name = "Secondary DB", ComputerName = "192.168.1.131" }];

        ValidateProfiles(profiles);
        return profiles;
    }

    /// <summary>Проверяет два независимых профиля без обращения к БД или SCM.</summary>
    private static void ValidateProfiles(IReadOnlyList<PostgreSqlSetupProfile> profiles)
    {
        if (profiles.Count != 2 || profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
            throw new InvalidDataException("Нужны два профиля PostgreSQL с разными названиями.");

        foreach (var profile in profiles)
            profile.Validate();

        if (profiles[0].ComputerName.Equals(profiles[1].ComputerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Укажите разные компьютеры PostgreSQL.");
    }

    /// <summary>Сохраняет профили атомарно; существующий файл предварительно сохраняется как .bak.</summary>
    public void SaveProfiles(IReadOnlyList<PostgreSqlSetupProfile> profiles)
    {
        ValidateProfiles(profiles);
        Directory.CreateDirectory(Storage);

        if (File.Exists(ProfilesPath))
            File.Copy(ProfilesPath, ProfilesPath + ".bak", true);

        WriteAtomic(ProfilesPath, JsonSerializer.Serialize(profiles, Json));
    }

    /// <summary>Пишет файл через временное имя в том же каталоге.</summary>
    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(true));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Выдаёт отпечаток всех параметров, чтобы старый отчёт не подтверждал изменённый профиль.</summary>
    public static string Fingerprint(PostgreSqlSetupProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, Json))));

    /// <summary>Сопоставляет профиль с именем текущего компьютера либо его IPv4 без DNS и удалённых подключений.</summary>
    public static bool IsLocal(PostgreSqlSetupProfile profile)
    {
        if (profile.ComputerName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!System.Net.IPAddress.TryParse(profile.ComputerName, out var expected) || expected.AddressFamily != AddressFamily.InterNetwork)
            return false;

        return NetworkInterface.GetAllNetworkInterfaces().SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Any(address => address.Address.Equals(expected));
    }

    /// <summary>Встраивает инструмент и профиль в один PS1, который работает без Maintenance, Runtime, SDK и исходников.</summary>
    public void Export(PostgreSqlSetupProfile profile, string destination, string toolPath)
    {
        profile.Validate();

        var engine = Convert.ToBase64String(File.ReadAllBytes(toolPath));
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, Json)));

        var script = """
#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Check','Prepare')][string]$Mode='Prepare', [switch]$Approved, [switch]$NoPause, [Guid]$RunId=[Guid]::NewGuid())

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'

$profile=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PROFILE__')) | ConvertFrom-Json
$code=0
$mutex=$null
$locked=$false
$transcript=$false

$run=Join-Path $env:ProgramData ('TechMES\PostgreSqlSetup\Runs\'+$RunId.ToString('N'))
$result=Join-Path $run 'result.json'

$report=[ordered]@{
    Format=1
    ProfileFingerprint='__FINGERPRINT__'
    Computer=$env:COMPUTERNAME
    Addresses=@()
    CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Ready=$false
    Message='Not completed.'
    LogPath=(Join-Path $run 'setup.log')
    State=$null
}

# Повышение выполняется до try/finally: родитель не перезаписывает отчёт дочернего процесса.
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $admin=(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
finally {
    $identity.Dispose()
}

if (!$admin) {
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Mode "+$Mode+" -RunId '"+$RunId.ToString()+"'"

    if ($Approved) { $command+=' -Approved' }
    if ($NoPause) { $command+=' -NoPause' }

    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -Wait -PassThru

    exit $child.ExitCode
}

try {
    $report.Addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)

    if ($profile.ComputerName -ine $env:COMPUTERNAME -and $profile.ComputerName -notin $report.Addresses) {
        throw 'Wrong computer for this PostgreSQL profile.'
    }

    New-Item -ItemType Directory -Path $run -Force | Out-Null
    Start-Transcript -LiteralPath $report.LogPath -Force | Out-Null
    $transcript=$true

    $mutex=New-Object Threading.Mutex($false,('Global\TechMES.PostgreSqlSetup.'+$profile.ServiceName))

    try {
        $locked=$mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $locked=$true
    }

    if (!$locked) {
        throw 'Another PostgreSQL setup operation is running.'
    }

    $tool=Join-Path $run 'Set-PostgreSQL18ServiceControl.ps1'
    [IO.File]::WriteAllBytes($tool,[Convert]::FromBase64String('__ENGINE__'))

    $arguments=@('-ServiceName',$profile.ServiceName,'-PgBin',$profile.BinDirectory,'-DataDirectory',$profile.DataDirectory,'-Port',[string]$profile.Port)
    $powershell="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

    $raw=& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -Mode Status -AsJson @arguments

    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL Status validation failed.'
    }

    $report.State=($raw -join [Environment]::NewLine) | ConvertFrom-Json

    if ($Mode -eq 'Prepare' -and !$report.State.ManagedByService) {
        Write-Host ('Computer: '+$env:COMPUTERNAME+'; service: '+$profile.ServiceName+'; data: '+$profile.DataDirectory)

        if (!$Approved -and (Read-Host 'PostgreSQL will restart. Type ADOPT to continue') -cne 'ADOPT') {
            throw 'Preparation cancelled.'
        }

        & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -Mode AdoptService @arguments

        if ($LASTEXITCODE -ne 0) {
            throw 'PostgreSQL adoption failed.'
        }

        # После изменения проверяем устойчивость владения службой дольше минуты.
        for ($index=0; $index -lt 9; $index++) {
            $raw=& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -Mode Status -AsJson @arguments

            if ($LASTEXITCODE -ne 0) {
                throw 'PostgreSQL status failed after adoption.'
            }

            $report.State=($raw -join [Environment]::NewLine) | ConvertFrom-Json

            if (!$report.State.ManagedByService -or $report.State.ServiceState -ne 'Running') {
                throw 'PostgreSQL lost Windows service ownership.'
            }

            Write-Host ((Get-Date -Format 'HH:mm:ss')+' Service=Running; ManagedByService=True')

            if ($index -lt 8) {
                Start-Sleep -Seconds 10
            }
        }
    }

    if (!$report.State.ManagedByService -or $report.State.ServiceState -ne 'Running') {
        throw 'PostgreSQL is not managed by its Windows service. Run Prepare.'
    }

    & (Join-Path $profile.BinDirectory 'pg_isready.exe') -h 127.0.0.1 -p $profile.Port -U postgres -d postgres -t 3

    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL is not accepting connections.'
    }

    $report.Ready=$true
    $report.Message='Windows service ownership and PostgreSQL readiness confirmed.'

    Write-Host $report.Message -ForegroundColor Green
}
catch {
    $code=1
    $report.Ready=$false
    $report.Message=$_.Exception.Message

    Write-Host ('FAILED: '+$report.Message) -ForegroundColor Red
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
    if ($transcript) { Stop-Transcript | Out-Null }

    $report.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')

    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $temporary=$result+'.tmp'

    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $result -Force
}

Write-Host ('Result: '+$result)

if (!$NoPause) {
    try { $null=Read-Host 'Press ENTER to close' } catch {}
}

exit $code
""";

        WriteAtomic(destination, script.Replace("__PROFILE__", data, StringComparison.Ordinal).Replace("__FINGERPRINT__", Fingerprint(profile), StringComparison.Ordinal).Replace("__ENGINE__", engine, StringComparison.Ordinal));
    }

    /// <summary>Читает отчёт только для текущего профиля и проверяет согласованность признаков готовности.</summary>
    public PostgreSqlSetupReport ReadReport(PostgreSqlSetupProfile profile, string path)
    {
        profile.Validate();

        var report = JsonSerializer.Deserialize<PostgreSqlSetupReport>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Пустой отчёт PostgreSQL.");

        if (report.Format != 1 || report.ProfileFingerprint != Fingerprint(profile) || report.CheckedAtUtc == default || report.CheckedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) || string.IsNullOrWhiteSpace(report.Computer) || report.Addresses is null || !profile.ComputerName.Equals(report.Computer, StringComparison.OrdinalIgnoreCase) && !report.Addresses.Contains(profile.ComputerName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Отчёт относится к другому компьютеру или другой версии профиля.");

        if (report.Ready && (report.State is null || !string.Equals(report.State.Computer, report.Computer, StringComparison.OrdinalIgnoreCase) || !report.State.ManagedByService || report.State.ServiceState != "Running" || !string.Equals(report.State.ServiceName, profile.ServiceName, StringComparison.OrdinalIgnoreCase) || !string.Equals(report.State.DataDirectory?.Replace('/', '\\').TrimEnd('\\'), profile.DataDirectory.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || report.State.Port != profile.Port || report.State.PostmasterProcessId <= 0 || report.State.ListenerProcessId != report.State.PostmasterProcessId || report.State.ServiceProcessId <= 0))
            throw new InvalidDataException("Отчёт содержит противоречивое состояние PostgreSQL.");

        return report;
    }

    /// <summary>Сохраняет проверенный отчёт локально для просмотра из одной Maintenance.</summary>
    public PostgreSqlSetupReport ImportReport(PostgreSqlSetupProfile profile, string path)
    {
        var report = ReadReport(profile, path);
        var receipt = ReceiptPath(profile);

        if (File.Exists(receipt) && ReadReport(profile, receipt).CheckedAtUtc > report.CheckedAtUtc)
            throw new InvalidDataException("Уже сохранён более свежий отчёт этого сервера.");

        WriteAtomic(receipt, JsonSerializer.Serialize(report, Json));
        return report;
    }

    /// <summary>Возвращает путь последнего отчёта для точного профиля.</summary>
    public string ReceiptPath(PostgreSqlSetupProfile profile) => Path.Combine(Storage, "Receipts", Fingerprint(profile) + ".json");

    /// <summary>Запускает самостоятельную консоль с UAC и ждёт без таймаута и остановки дочернего процесса.</summary>
    public async Task<PostgreSqlSetupReport> RunLocalAsync(PostgreSqlSetupProfile profile, string toolPath, bool prepare)
    {
        profile.Validate();

        if (!IsLocal(profile))
            throw new InvalidOperationException("Этот профиль относится к другому компьютеру. Экспортируйте скрипт и выполните его на сервере БД.");

        var id = Guid.NewGuid();
        var directory = Path.Combine(Root, "Runs", id.ToString("N"));
        var script = Path.Combine(directory, "PostgreSQL.ps1");

        Export(profile, script, toolPath);

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = true, Verb = "runas" };

        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", prepare ? "Prepare" : "Check", "-Approved", "-NoPause", "-RunId", id.ToString() })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проверку PostgreSQL.");
        await process.WaitForExitAsync();

        var resultPath = Path.Combine(directory, "result.json");
        var report = ReadReport(profile, resultPath);

        if (process.ExitCode != 0 && report.Ready)
            throw new InvalidDataException("Код завершения не соответствует успешному отчёту PostgreSQL.");

        return ImportReport(profile, resultPath);
    }
}