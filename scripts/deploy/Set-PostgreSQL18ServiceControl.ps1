#requires -Version 5.1
<#
.SYNOPSIS
Checks PostgreSQL service ownership or adopts an unmanaged local instance.
.DESCRIPTION
AdoptService performs a clean fast shutdown and starts the existing Windows
service against the same data directory. It does not promote or clone a cluster.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param([ValidateSet('Status','AdoptService')][string]$Mode='Status', [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18', [string]$PgBin='D:\PostgreSQL\18\bin', [string]$DataDirectory='D:\PostgreSQL\18\data', [ValidateRange(1,65535)][int]$Port=5432, [switch]$AsJson)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Machine-readable mode is read-only.
if ($AsJson) {
    if ($Mode -ne 'Status') {
        throw 'AsJson is supported only for Status.'
    }
}

# Normalizes quoted paths and slash direction for case-insensitive comparison.
function Get-NormalizedPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path.Trim().Trim('"')).TrimEnd('\', '/').Replace('/', '\')
}

# Extracts -D from a registered service or PostgreSQL command line.
function Get-CommandDataDirectory([string]$CommandLine) {
    $match = [regex]::Match($CommandLine, '(?:^|\s)-D\s+(?:"([^"]+)"|(\S+))', 'IgnoreCase')

    if (!$match.Success) {
        throw 'Cannot identify the data directory in the command line.'
    }

    if ($match.Groups[1].Success) {
        return $match.Groups[1].Value
    }

    return $match.Groups[2].Value
}

# Checks that the port owner belongs to the Windows service process tree.
function Test-ServiceAncestor([int]$ProcessId, [int]$ServiceProcessId) {
    if ($ServiceProcessId -le 0) {
        return $false
    }

    for ($level = 0; $level -lt 16 -and $ProcessId -gt 0; $level++) {
        if ($ProcessId -eq $ServiceProcessId) {
            return $true
        }

        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"

        if (!$process -or [int]$process.ParentProcessId -eq $ProcessId) {
            return $false
        }

        $ProcessId = [int]$process.ParentProcessId
    }

    return $false
}

# Reads fresh SCM, PID-file and TCP information.
# Refuses operation when paths, processes or port ownership do not match.
function Get-PostgreSqlServiceState {
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"

    if (!$service) {
        throw "Service not found: $ServiceName"
    }

    $binary = [regex]::Match($service.PathName, '^\s*(?:"([^"]+\.exe)"|(.+?\.exe))(?:\s|$)', 'IgnoreCase')

    if (!$binary.Success) {
        throw 'Cannot identify the service executable.'
    }

    $binaryPath = if ($binary.Groups[1].Success) {
        $binary.Groups[1].Value
    } else {
        $binary.Groups[2].Value
    }

    if ((Get-NormalizedPath $binaryPath) -ine (Get-NormalizedPath (Join-Path $PgBin 'pg_ctl.exe'))) {
        throw 'Service executable does not match PgBin.'
    }

    if ((Get-NormalizedPath (Get-CommandDataDirectory $service.PathName)) -ine (Get-NormalizedPath $DataDirectory)) {
        throw 'Service data directory does not match DataDirectory.'
    }

    $fileProcessId = 0
    $pidFile = Join-Path $DataDirectory 'postmaster.pid'

    if (Test-Path -LiteralPath $pidFile) {
        $lines = [IO.File]::ReadAllLines($pidFile)

        if ($lines.Count -lt 4 -or ![int]::TryParse($lines[0], [ref]$fileProcessId)) {
            throw 'Invalid postmaster.pid file.'
        }

        if ((Get-NormalizedPath $lines[1]) -ine (Get-NormalizedPath $DataDirectory) -or [int]$lines[3] -ne $Port) {
            throw 'PID file belongs to another data directory or port.'
        }
    }

    $owners = @(
        Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess |
            Sort-Object -Unique
    )

    if ($owners.Count -gt 1) {
        throw 'Different processes listen on this port; adoption is refused.'
    }

    $listenerProcessId = 0
    $managed = $false

    if ($owners.Count -eq 1) {
        $listenerProcessId = [int]$owners[0]

        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$listenerProcessId"

        if (!$process -or $fileProcessId -ne $listenerProcessId) {
            throw 'Port owner does not match postmaster.pid.'
        }

        if ((Get-NormalizedPath $process.ExecutablePath) -ine (Get-NormalizedPath (Join-Path $PgBin 'postgres.exe'))) {
            throw 'Port is owned by another PostgreSQL binary.'
        }

        if ((Get-NormalizedPath (Get-CommandDataDirectory $process.CommandLine)) -ine (Get-NormalizedPath $DataDirectory)) {
            throw 'Port owner uses another data directory.'
        }

        $managed = $service.State -eq 'Running' -and
            (Test-ServiceAncestor $listenerProcessId ([int]$service.ProcessId))
    }

    return [pscustomobject]@{
        Computer = $env:COMPUTERNAME
        ServiceName = $ServiceName
        ServiceState = $service.State
        ServiceProcessId = [int]$service.ProcessId
        ServiceAccount = $service.StartName
        DataDirectory = $DataDirectory
        Port = $Port
        PostmasterProcessId = $fileProcessId
        ListenerProcessId = $listenerProcessId
        ManagedByService = $managed
    }
}

# Stops only the validated local data directory using clean fast shutdown.
function Stop-UnmanagedPostgreSql {
    & (Join-Path $PgBin 'pg_ctl.exe') stop -D $DataDirectory -m fast -w -t 60 |
        Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL did not confirm clean shutdown; service was not started.'
    }
}

# Starts the existing service and verifies ownership of the listening process.
# Already managed instances are returned without a restart.
function Set-PostgreSqlServiceControl {
    $before = Get-PostgreSqlServiceState

    if ($before.ManagedByService) {
        return $before
    }

    if ($before.ServiceState -ne 'Stopped') {
        throw 'Service must be Stopped or already own the server.'
    }

    if ($before.ListenerProcessId -gt 0) {
        Write-Host 'Stopping the unmanaged PostgreSQL instance...' -ForegroundColor Yellow
        Stop-UnmanagedPostgreSql
    } elseif ($before.PostmasterProcessId -gt 0) {
        throw 'PID file exists without a listening server. Inspect startup/shutdown before adoption.'
    }

    $stopped = Get-PostgreSqlServiceState

    if ($stopped.ListenerProcessId -gt 0 -or $stopped.PostmasterProcessId -gt 0 -or $stopped.ServiceState -ne 'Stopped') {
        throw 'Shutdown is not confirmed; service start is refused.'
    }

    Write-Host "Starting Windows service: $ServiceName" -ForegroundColor Cyan

    Start-Service -Name $ServiceName -ErrorAction Stop

    (Get-Service -Name $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))

    $deadline = [DateTime]::UtcNow.AddSeconds(30)

    do {
        $after = Get-PostgreSqlServiceState

        if ($after.ManagedByService) {
            return $after
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The service did not confirm ownership of the listening PostgreSQL process.'
}

# Require an elevated Windows session and an existing PostgreSQL 18 installation.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this script on Windows.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()

if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

foreach ($file in @(
    (Join-Path $PgBin 'pg_ctl.exe'),
    (Join-Path $PgBin 'postgres.exe'),
    (Join-Path $DataDirectory 'PG_VERSION')
)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required file not found: $file"
    }
}

if ([IO.File]::ReadAllText((Join-Path $DataDirectory 'PG_VERSION')).Trim() -ne '18') {
    throw 'The data directory is not PostgreSQL 18.'
}

$state = Get-PostgreSqlServiceState

if ($AsJson) {
    # ASCII-escaped JSON works without a console and preserves Unicode paths.
    $json = $state | ConvertTo-Json -Depth 4 -Compress

    [regex]::Replace($json, '[^\x00-\x7F]', { param($value) '\u{0:x4}' -f [int][char]$value.Value })

    return
}

$state | Format-List | Out-Host

if ($Mode -eq 'Status') {
    return
}

if ($state.ManagedByService) {
    Write-Host 'OK: PostgreSQL is already managed by its Windows service.' -ForegroundColor Green
    return
}

if (!$PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Adopt PostgreSQL into service $ServiceName; database connections will be interrupted")) {
    return
}

$state = Set-PostgreSqlServiceControl
$state | Format-List | Out-Host

Write-Host 'OK: PostgreSQL is managed by its Windows service.' -ForegroundColor Green