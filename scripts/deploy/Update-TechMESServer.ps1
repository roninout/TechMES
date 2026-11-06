#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$PackageRoot='', [ValidateSet('runtime','calc','web')][string[]]$Components=@('runtime','calc','web'), [string]$BackupRoot="$env:ProgramData\TechMES\Updates", [string]$RuntimeStatusUrl='http://127.0.0.1:5101/api/runtime/status', [string]$ExpectedInstallRoot='', [switch]$StartAll)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'

# Tests directory equality or nesting in either direction.
function Test-PathOverlap([string]$Left, [string]$Right) {
    $a=$Left.Replace('\','/').TrimEnd('/')
    $b=$Right.Replace('\','/').TrimEnd('/')
    return $a -ieq $b -or $a.StartsWith($b+'/',[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a+'/',[StringComparison]::OrdinalIgnoreCase)
}

# Rejects links in the complete existing path.
function Assert-NoLinks([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not supported: $current" }
        }
        $current=Split-Path $current -Parent
    }
}

# Resolves a manifest path inside its root.
function Get-SafePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -match '[:\\]|(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($Relative)) { throw "Invalid manifest path: $Relative" }
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $path=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside its root: $Relative" }
    Assert-NoLinks $path
    return $path
}

# Protects configuration, local data and private certificates.
function Test-ProtectedPath([string]$Relative) {
    $name=[IO.Path]::GetFileName($Relative)
    return $name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or $name -match '\.(pfx|p12|pem|key)$' -or $Relative -match '(^|/)(logs|data|certs|backups)/'
}

# Gets the installation directory from the registered service image.
function Get-ServiceFolder($Service, [string]$ExpectedExe) {
    $match=[regex]::Match($Service.PathName,'^\s*(?:"([^"\r\n]+\.exe)"|(.+?\.exe))(?:\s|$)','IgnoreCase')
    if (!$match.Success) { throw "Invalid service image: $($Service.Name)" }
    $exe=if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    $exe=[Environment]::ExpandEnvironmentVariables($exe)
    if ([IO.Path]::GetFileName($exe) -ine $ExpectedExe) { throw "Unexpected executable: $exe" }
    $folder=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe))
    Assert-NoLinks $folder
    return $folder
}

# Stops a service normally and waits for its process to exit.
function Stop-Component([string]$Name) {
    $info=Get-CimInstance Win32_Service -Filter "Name='$Name'"
    $oldProcess=[int]$info.ProcessId
    $service=Get-Service -Name $Name
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $Name -ErrorAction Stop }
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(60))
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    while ($oldProcess -gt 0 -and (Get-Process -Id $oldProcess -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Service process has not exited: $Name" }
        Start-Sleep -Milliseconds 200
    }
}

# Starts the existing service without changing its account or startup mode.
function Start-Component([string]$Name) {
    if ((Get-Service -Name $Name).Status -ne 'Running') { Start-Service -Name $Name -ErrorAction Stop }
    (Get-Service -Name $Name).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(60))
}

# Accepts both Active and Standby Runtime after an update.
function Wait-RuntimeReady([string]$Url) {
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $status=Invoke-RestMethod -Uri $Url -TimeoutSec 3
            if ($status.mode -in @('Active','Standby')) { Write-Output "Runtime ready: $($status.mode)"; return }
        } catch {}
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Runtime readiness failed: $Url"
}

# Restores every file touched by this transaction.
function Restore-Files($Entries) {
    foreach ($entry in $Entries) {
        if (!$entry.Touched) { continue }
        if ($entry.Existed) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force }
        elseif (Test-Path -LiteralPath $entry.Target) { Remove-Item -LiteralPath $entry.Target -Force }
    }
}

# Copies payload files and starts selected services inside the rollback boundary.
function Invoke-UpdateTransaction($Jobs, $Entries, $Configs, [bool]$StartSelected, [string]$StatusUrl) {
    try {
        foreach ($key in @('web','calc','runtime')) {
            foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key })) { Stop-Component $job.Name }
        }
        foreach ($entry in $Entries) {
            New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent) -Force | Out-Null
            $entry.Touched=$true
            Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
            if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ine $entry.Hash) { throw "Installed file hash mismatch: $($entry.Target)" }
        }
        foreach ($key in @('runtime','calc','web')) {
            foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key -and ($StartSelected -or $_.WasRunning) })) { Start-Component $job.Name }
        }
        if (@($Jobs | Where-Object { $_.Key -eq 'runtime' -and ($StartSelected -or $_.WasRunning) }).Count -gt 0) { Wait-RuntimeReady $StatusUrl }
        foreach ($job in $Jobs) {
            $expected=if ($StartSelected -or $job.WasRunning) { 'Running' } else { 'Stopped' }
            if ([string](Get-Service -Name $job.Name).Status -ne $expected) { throw "Unexpected state: $($job.Name)" }
        }
        foreach ($config in $Configs) {
            if ((Get-FileHash -LiteralPath $config.Path -Algorithm SHA256).Hash -ine $config.Hash) { throw "Configuration changed: $($config.Path)" }
        }
    } catch {
        $original=$_.Exception.Message
        Write-Warning "Update failed; restoring the previous files and service states: $original"
        try {
            if (@($Entries | Where-Object { $_.Touched }).Count -gt 0) {
                foreach ($key in @('web','calc','runtime')) {
                    foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key })) { Stop-Component $job.Name }
                }
                Restore-Files $Entries
            }
            foreach ($key in @('runtime','calc','web')) {
                foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key -and $_.WasRunning })) { Start-Component $job.Name }
            }
            foreach ($job in $Jobs) {
                $expected=if ($job.WasRunning) { 'Running' } else { 'Stopped' }
                if ([string](Get-Service -Name $job.Name).Status -ne $expected) { throw "Rollback state mismatch: $($job.Name)" }
            }
        } catch { throw "UPDATE FAILED: $original; ROLLBACK INCOMPLETE: $($_.Exception.Message)" }
        throw "Update rolled back: $original"
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required.' }
} finally { $identity.Dispose() }

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot=[IO.Path]::GetDirectoryName($PSCommandPath) }
$PackageRoot=(Resolve-Path -LiteralPath $PackageRoot).Path
$manifest=Get-Content -LiteralPath (Join-Path $PackageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json

if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64' -or !$manifest.SelfContained -or ![Environment]::Is64BitOperatingSystem) { throw 'Unsupported package or operating system.' }

$uri=[Uri]$RuntimeStatusUrl
if (!$uri.IsAbsoluteUri -or !$uri.IsLoopback -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'RuntimeStatusUrl must be a local HTTP/HTTPS URL.' }

$allowed=@{ runtime=@{Service='TechMES.Runtime.Service';Exe='TechMES.Runtime.Service.exe'}; calc=@{Service='TechMES.Calc.Service';Exe='TechMES.Calc.Service.exe'}; web=@{Service='TechMES.Web';Exe='TechMES.Web.exe'} }
$backup=Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ((Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N'))
$jobs=@(); $entries=@(); $configs=@()
$mutex=New-Object Threading.Mutex($false,'Global\TechMES.ServerUpdate')
$locked=$false

try {
    try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (!$locked) { throw 'Another TechMES update is already running.' }

    foreach ($key in @($Components | Select-Object -Unique)) {
        $parts=@($manifest.Components | Where-Object { $_.Key -eq $key })
        if ($parts.Count -ne 1) { throw "Package component missing or duplicated: $key" }
        $part=$parts[0]
        if ($part.Service -cne $allowed[$key].Service -or $part.Exe -cne $allowed[$key].Exe) { throw "Invalid component: $key" }

        $service=Get-CimInstance Win32_Service -Filter "Name='$($part.Service)'"
        if (!$service) { if ($StartAll) { throw "Selected service is not installed: $($part.Service)" }; Write-Output "Skipped missing service: $($part.Service)"; continue }
        if ($service.State -notin @('Running','Stopped')) { throw "Service is not stable: $($service.Name)" }

        $target=Get-ServiceFolder $service $part.Exe
        $source=Get-SafePath $PackageRoot $part.Folder

        foreach ($path in @($PackageRoot,$source,$backup)) { Assert-NoLinks $path }

        if ((Test-PathOverlap $PackageRoot $target) -or (Test-PathOverlap $backup $target) -or (Test-PathOverlap $backup $PackageRoot)) { throw "Update paths overlap: Package=$PackageRoot; Installed=$target; Backup=$backup" }

        if ($ExpectedInstallRoot) {
            $root=[IO.Path]::GetFullPath($ExpectedInstallRoot).TrimEnd('\')
            if ($target -ine $root -and !$target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Service is outside ExpectedInstallRoot: $target" }
        }

        foreach ($old in $jobs) { if (Test-PathOverlap $old.Folder $target) { throw 'Service installation folders overlap.' } }
        if (!(Test-Path -LiteralPath (Join-Path $target 'appsettings.json') -PathType Leaf)) { throw "Existing appsettings.json is required: $target" }

        $seen=@{}
        foreach ($file in $part.Files) {
            if ((Test-ProtectedPath $file.Path) -or $seen.ContainsKey($file.Path)) { throw "Protected or duplicate payload path: $($file.Path)" }
            $seen[$file.Path]=$true
            $from=Get-SafePath $source $file.Path
            $to=Get-SafePath $target $file.Path
            if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ine $file.Sha256) { throw "Package hash mismatch: $key/$($file.Path)" }
            $entries += [pscustomobject]@{Source=$from;Target=$to;Hash=$file.Sha256;Backup=(Get-SafePath (Join-Path $backup $key) $file.Path);Existed=(Test-Path -LiteralPath $to -PathType Leaf);Touched=$false}
        }

        if (!$seen.ContainsKey($part.Exe)) { throw "Executable missing from manifest: $($part.Exe)" }
        $jobs += [pscustomobject]@{Key=$key;Name=$part.Service;Folder=$target;WasRunning=($service.State -eq 'Running')}
        $configs += @(Get-ChildItem -LiteralPath $target -Filter 'appsettings*.json' -File -Force | ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 })
    }

    if (!$jobs.Count) { throw 'No installed services selected.' }
    $jobs | Format-Table Key,Name,Folder,WasRunning -AutoSize | Out-Host

    if (!$PSCmdlet.ShouldProcess($env:COMPUTERNAME,"Update TechMES from $($manifest.Id)")) { return }

    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $jobs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'services.json') -Encoding UTF8
    $configs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'settings-hashes.json') -Encoding UTF8

    foreach ($entry in $entries) {
        if ($entry.Existed) {
            New-Item -ItemType Directory -Path (Split-Path $entry.Backup -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Target -Destination $entry.Backup -Force
        }
    }

    Write-Output "Backup: $backup"
    Invoke-UpdateTransaction $jobs $entries $configs ([bool]$StartAll) $RuntimeStatusUrl
    Write-Output 'Update completed. appsettings files were preserved.'
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}