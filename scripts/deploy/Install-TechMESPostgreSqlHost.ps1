#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Auto','Runtime','Standalone')][string]$Mode='Auto', [string]$AgentDirectory='D:\TechMES\PostgreSql.Agent', [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18', [string]$PgBin='D:\PostgreSQL\18\bin', [string]$DataDirectory='D:\PostgreSQL\18\data', [ValidateRange(1,65535)][int]$Port=5432, [ValidateRange(5,3600)][int]$HealthPeriodSeconds=10)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# Требует локальный административный запуск установки хоста.
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
} finally { $identity.Dispose() }

# Проверяет локальный путь и отсутствие ссылок в существующих родителях.
function Assert-Directory([string]$Value) {
    $full=[IO.Path]::GetFullPath($Value).TrimEnd('\')
    if ($full -notmatch '^[A-Za-z]:\\.+' -or $full.Substring(3) -match '[:<>"|?*\x00-\x1f]') { throw 'Use a local non-root Windows directory.' }
    $current=$full
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse point is not supported: '+$current) }
        $current=Split-Path -Path $current -Parent
    }
    return $full
}

# Запрещает размещение файлов хоста внутри PostgreSQL bin/data и наоборот.
function Test-Overlap([string]$Left, [string]$Right) {
    return $Left.Equals($Right,[StringComparison]::OrdinalIgnoreCase) -or $Left.StartsWith($Right+'\',[StringComparison]::OrdinalIgnoreCase) -or $Right.StartsWith($Left+'\',[StringComparison]::OrdinalIgnoreCase)
}

# Извлекает путь apphost из конфигурации существующей службы Runtime.
function Get-RuntimeDirectory([string]$Command) {
    if ($Command -match '^\s*"([^"]+)"') { $executable=$matches[1] }
    elseif ($Command -match '^\s*(\S+)') { $executable=$matches[1] }
    else { throw 'Cannot read Runtime service executable.' }
    if ([IO.Path]::GetFileName($executable) -ine 'TechMES.Runtime.Service.exe') { throw 'The Runtime service must use the TechMES.Runtime.Service.exe apphost.' }
    return Assert-Directory ([IO.Path]::GetDirectoryName($executable))
}

$PgBin=Assert-Directory $PgBin
$DataDirectory=Assert-Directory $DataDirectory
$AgentDirectory=Assert-Directory $AgentDirectory

$runtime=Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service'"
$agent=Get-CimInstance Win32_Service -Filter "Name='TechMES.PostgreSql.Agent'"
$selected=if ($Mode -eq 'Auto') { if ($null -ne $runtime) { 'Runtime' } else { 'Standalone' } } else { $Mode }

if ($selected -eq 'Standalone' -and $null -ne $runtime) { throw 'Runtime is installed here. Use -Mode Runtime; a separate PostgreSQL agent is not required.' }
if ($selected -eq 'Runtime' -and $null -eq $runtime) { throw 'TechMES.Runtime.Service is not installed on this computer.' }

$statusTool=Join-Path $PSScriptRoot 'Set-PostgreSQL18ServiceControl.ps1'
if (!(Test-Path -LiteralPath $statusTool -PathType Leaf)) { throw 'Keep this installer in the published Tools directory beside Set-PostgreSQL18ServiceControl.ps1.' }

$powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$raw=& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $statusTool -Mode Status -AsJson -ServiceName $ServiceName -PgBin $PgBin -DataDirectory $DataDirectory -Port $Port

if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL service profile was not validated. No host settings were changed.' }

$checked=$raw | ConvertFrom-Json
$fullyStopped=$checked.ServiceState -eq 'Stopped' -and $checked.PostmasterProcessId -eq 0 -and $checked.ListenerProcessId -eq 0

if (!$checked.ManagedByService -and !$fullyStopped) { throw 'A running unmanaged PostgreSQL instance was detected. Resolve its service ownership in Maintenance before host setup.' }

if ($selected -eq 'Runtime') {
    $installDirectory=Get-RuntimeDirectory $runtime.PathName
    if (!(Test-Path -LiteralPath (Join-Path $installDirectory 'TechMES.PostgreSql.Control.dll') -PathType Leaf)) { throw 'Deploy the updated Runtime with TechMES.PostgreSql.Control.dll first.' }
}
else {
    $sourceDirectory=Assert-Directory (Split-Path -Path $PSScriptRoot -Parent)
    if (!(Test-Path -LiteralPath (Join-Path $sourceDirectory 'TechMES.PostgreSql.Agent.exe') -PathType Leaf)) { throw 'Publish TechMES.PostgreSql.Agent for win-x64 and run its Tools installer.' }
    $installDirectory=$AgentDirectory
    if ($sourceDirectory -ine $installDirectory -and ($installDirectory.StartsWith($sourceDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or $sourceDirectory.StartsWith($installDirectory+'\',[StringComparison]::OrdinalIgnoreCase))) { throw 'The package and installation directories must not contain one another.' }
}

foreach ($protectedDirectory in @($PgBin,$DataDirectory)) {
    if (Test-Overlap $installDirectory $protectedDirectory) { throw 'The TechMES host installation directory must be outside PostgreSQL bin/data.' }
    if ($selected -eq 'Standalone' -and (Test-Overlap $sourceDirectory $protectedDirectory)) { throw 'The host package must be outside PostgreSQL bin/data.' }
}

# Останавливаем только прежний отдельный хост; PostgreSQL установка не останавливает.
if ($null -ne $agent) {
    Stop-Service -Name 'TechMES.PostgreSql.Agent' -ErrorAction Stop
    (Get-Service -Name 'TechMES.PostgreSql.Agent').WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30))
}

if ($selected -eq 'Runtime') {
    if ($null -ne $agent) { Set-Service -Name 'TechMES.PostgreSql.Agent' -StartupType Disabled }
}
else {
    if ($sourceDirectory -ine $installDirectory) {
        $null=New-Item -ItemType Directory -Path $installDirectory -Force
        foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File) {
            $relative=$file.FullName.Substring($sourceDirectory.Length+1)
            $destination=Join-Path $installDirectory $relative
            if ($relative -match '^appsettings.*\.json$' -and (Test-Path -LiteralPath $destination)) { continue }
            $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
}

# Отдельный appsettings сохраняется существующим механизмом обновления TechMES.
$settingsPath=Join-Path $installDirectory 'appsettings.PostgreSqlControl.json'
$storageRoot=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$ServiceName.ToUpperInvariant())
$backupRoot=Join-Path $storageRoot 'SetupBackups'

$null=New-Item -ItemType Directory -Path $backupRoot -Force

$settings=[pscustomobject]@{}
if (Test-Path -LiteralPath $settingsPath) {
    $settings=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $backupRoot ([guid]::NewGuid().ToString('N')+'.json'))
}

$property=$settings.PSObject.Properties['PostgreSqlControl']
$control=if ($null -ne $property -and $null -ne $property.Value) { $property.Value } else { [pscustomobject]@{} }
$values=[ordered]@{ Enabled=$true; AutoDetect=$true; ServiceName=$ServiceName; BinDirectory=$PgBin; DataDirectory=$DataDirectory; Port=$Port; HealthPeriodSeconds=$HealthPeriodSeconds }

foreach ($key in $values.Keys) {
    $control | Add-Member -NotePropertyName $key -NotePropertyValue $values[$key] -Force
}

$settings | Add-Member -NotePropertyName 'PostgreSqlControl' -NotePropertyValue $control -Force

[IO.File]::WriteAllText($settingsPath,($settings | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($true)))

if ($selected -eq 'Runtime') {
    Restart-Service -Name 'TechMES.Runtime.Service' -ErrorAction Stop
    (Get-Service -Name 'TechMES.Runtime.Service').WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(60))
}
else {
    $executable=Join-Path $installDirectory 'TechMES.PostgreSql.Agent.exe'

    if ($null -eq $agent) {
        $null=New-Service -Name 'TechMES.PostgreSql.Agent' -BinaryPathName ('"'+$executable+'"') -DisplayName 'TechMES PostgreSQL local host' -StartupType Automatic
    }
    else {
        & sc.exe config 'TechMES.PostgreSql.Agent' binPath= ('"'+$executable+'"') start= auto
        if ($LASTEXITCODE -ne 0) { throw 'Failed to update the existing PostgreSQL host service.' }
    }

    & sc.exe failure 'TechMES.PostgreSql.Agent' reset= 86400 actions= restart/5000/restart/15000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure PostgreSQL host recovery.' }

    Start-Service -Name 'TechMES.PostgreSql.Agent' -ErrorAction Stop
    (Get-Service -Name 'TechMES.PostgreSql.Agent').WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(60))
}

Write-Host ('Host: '+$selected) -ForegroundColor Green
Write-Host ('Settings: '+$settingsPath)
Write-Host ('Local status: '+(Join-Path $storageRoot 'status.json'))