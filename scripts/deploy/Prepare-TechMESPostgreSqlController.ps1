#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Check','Prepare')][string]$Mode = 'Check', [Guid]$RunId = [Guid]::NewGuid(), [switch]$NoPause)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:EmbeddedProfileBase64 = ''

# The exported script carries a fixed profile; no shell command is built from its fields.
if (!$script:EmbeddedProfileBase64) { throw 'Export this tool from Maintenance first.' }
$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:EmbeddedProfileBase64)) | ConvertFrom-Json
$profile = $payload.Profile
$run = Join-Path $PSScriptRoot ('Runs\' + $RunId.ToString('N'))
$resultPath = Join-Path $run 'result.json'
$logPath = Join-Path $run 'setup.log'
$exitCode = 0
$transcript = $false
$locks = New-Object 'System.Collections.Generic.List[System.Threading.Mutex]'
$report = [ordered]@{ Format = 1; ProfileFingerprint = $payload.ProfileFingerprint; RunId = $RunId.ToString(); Computer = $env:COMPUTERNAME; Addresses = @(); CheckedAtUtc = ''; Ready = $false; PreparationCompleted = $false; HostKind = ''; HostService = ''; HostServiceState = ''; HostProcessId = 0; SettingsPath = ''; Local = $null; Message = ''; LogPath = $logPath }
if (Test-Path -LiteralPath $run) { throw 'This RunId already exists. Use a new run.' }

# Reads only the apphost path from a registered TechMES service.
function Get-ServiceDirectory([string]$Command, [string]$ExpectedExe) {
    if ($Command -match '^\s*"([^"]+)"') { $executable = $Matches[1] }
    elseif ($Command -match '^\s*(\S+)') { $executable = $Matches[1] }
    else { throw 'Cannot read the controller service executable.' }
    if ([IO.Path]::GetFileName($executable) -ine $ExpectedExe) { throw ('Unexpected controller apphost: ' + $executable) }
    return [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($executable))
}

# Compares Windows paths without changing PostgreSQL settings.
function Test-SamePath([string]$Left, [string]$Right) {
    return $Left.Replace('/', '\').TrimEnd('\').Equals($Right.Replace('/', '\').TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

# Rechecks packaged bytes before the existing installer can stop or copy a service.
function Assert-AgentPackage([string]$Directory) {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'agent-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64' -or !$manifest.SelfContained -or @($manifest.Components).Count -ne 1) { throw 'Invalid Agent manifest.' }
    $component = $manifest.Components[0]
    if ($component.Key -ne 'postgres-agent' -or $component.Folder -ne 'PostgreSql.Agent' -or $component.Service -ne 'TechMES.PostgreSql.Agent' -or $component.Exe -ne 'TechMES.PostgreSql.Agent.exe') { throw 'Invalid Agent component.' }
    $names = @{}

    foreach ($file in $component.Files) {
        $name = ([string]$file.Path).Replace('\', '/')
        if (!$name -or $name -match '[:<>"|?*\x00-\x1f]' -or @($name.Split('/') | Where-Object { !$_ -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or $names.ContainsKey($name) -or $file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid Agent file path or hash.' }
        $names[$name] = $true
        $path = Join-Path $Directory $name
        for ($current = $path; $current; $current = Split-Path -Path $current -Parent) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent package contains a reparse point.' }
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.Sha256) { throw ('Agent SHA256 mismatch: ' + $name) }
    }

    foreach ($name in @('TechMES.PostgreSql.Agent.exe', 'TechMES.PostgreSql.Control.dll', 'Tools/Install-TechMESPostgreSqlHost.ps1', 'Tools/Set-PostgreSQL18ServiceControl.ps1')) {
        if (!$names.ContainsKey($name)) { throw ('Agent package file is missing: ' + $name) }
    }
}

try {
    $null = New-Item -ItemType Directory -Path $run
    Start-Transcript -LiteralPath $logPath -Force | Out-Null
    $transcript = $true

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
    }
    finally { $identity.Dispose() }

    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object { [string]$_.IPAddress })
    $report.Addresses = $addresses
    if ($profile.ComputerName -ine $env:COMPUTERNAME -and $addresses -notcontains $profile.ComputerName) { throw 'This profile belongs to another computer.' }
    if ($profile.ServiceName -notmatch '^[A-Za-z0-9_.-]+$' -or $profile.HostMode -notin @('Auto','Runtime','Standalone') -or $profile.HostHealthPeriodSeconds -lt 5 -or $profile.HostHealthPeriodSeconds -gt 3600) { throw 'Invalid controller profile.' }

    # Shares locks with deployment and PostgreSQL preparation, including exported consoles.
    foreach ($name in @('Global\TechMES.ServerUpdate', ('Global\TechMES.PostgreSqlSetup.' + $profile.ServiceName))) {
        $mutex = New-Object Threading.Mutex($false, $name)
        $locked = $false
        try { $locked = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { $mutex.Dispose(); throw 'Another deployment or PostgreSQL setup is running.' }
        $locks.Add($mutex)
    }

    $runtime = Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service'"
    $agent = Get-CimInstance Win32_Service -Filter "Name='TechMES.PostgreSql.Agent'"
    $selected = if ($profile.HostMode -eq 'Auto') { if ($null -ne $runtime) { 'Runtime' } else { 'Standalone' } } else { [string]$profile.HostMode }
    if ($selected -eq 'Runtime' -and $null -eq $runtime) { throw 'Runtime is not installed on this computer.' }
    if ($selected -eq 'Standalone' -and $null -ne $runtime) { throw 'Runtime is installed here. Select Auto or Runtime.' }
    $report.HostKind = $selected
    $report.HostService = if ($selected -eq 'Runtime') { 'TechMES.Runtime.Service' } else { 'TechMES.PostgreSql.Agent' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $started = [DateTimeOffset]::UtcNow

    if ($Mode -eq 'Prepare') {
        $installer = Join-Path $PSScriptRoot 'Tools\Install-TechMESPostgreSqlHost.ps1'
        $agentDirectory = [string]$profile.AgentDirectory

        if ($selected -eq 'Standalone') {
            if (!$agentDirectory -and $null -ne $agent) { $agentDirectory = Get-ServiceDirectory $agent.PathName 'TechMES.PostgreSql.Agent.exe' }
            if (!$agentDirectory) { throw 'Specify Agent installation directory for a new standalone controller.' }
            $package = Join-Path $PSScriptRoot 'PostgreSql.Agent'

            if (Test-Path -LiteralPath (Join-Path $package 'TechMES.PostgreSql.Agent.exe')) {
                Assert-AgentPackage $package
                $installer = Join-Path $package 'Tools\Install-TechMESPostgreSqlHost.ps1'
            }
            elseif ($null -ne $agent) {
                $installedDirectory = Get-ServiceDirectory $agent.PathName 'TechMES.PostgreSql.Agent.exe'
                if (!(Test-SamePath $installedDirectory $agentDirectory)) { throw 'Select an Agent package before changing its installation directory.' }
                $installer = Join-Path $installedDirectory 'Tools\Install-TechMESPostgreSqlHost.ps1'
            }
            else { throw 'Select an extracted Agent package in Maintenance and export it with the profile.' }
        }

        $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Mode', $selected, '-ServiceName', $profile.ServiceName, '-PgBin', $profile.BinDirectory, '-DataDirectory', $profile.DataDirectory, '-Port', [string]$profile.Port, '-HealthPeriodSeconds', [string]$profile.HostHealthPeriodSeconds)
        if ($selected -eq 'Standalone') { $arguments += @('-AgentDirectory', $agentDirectory) }
        & $powershell @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Controller installer failed. Inspect the transcript; readiness is not confirmed.' }
        $report.PreparationCompleted = $true
    }

    $statusPath = Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\' + $profile.ServiceName.ToUpperInvariant() + '\status.json')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($(if ($Mode -eq 'Prepare') { 90 } else { 0 }))

    do {
        $hostService = Get-CimInstance Win32_Service -Filter ("Name='" + $report.HostService + "'")
        if ($null -eq $hostService) { throw 'The selected controller service is not installed.' }
        $report.HostServiceState = [string]$hostService.State
        $report.HostProcessId = [int]$hostService.ProcessId
        $hostDirectory = Get-ServiceDirectory $hostService.PathName ($report.HostService + '.exe')
        $report.SettingsPath = Join-Path $hostDirectory 'appsettings.PostgreSqlControl.json'
        $valid = $false

        if ($hostService.State -eq 'Running' -and $hostService.ProcessId -gt 0 -and (Test-Path -LiteralPath $statusPath)) {
            $local = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            $report.Local = $local
            $checkedAt = [DateTimeOffset]$local.checkedAtUtc
            $hostProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $hostService.ProcessId)
            $fresh = $null -ne $hostProcess -and $checkedAt.UtcDateTime -ge $hostProcess.CreationDate.ToUniversalTime() -and $checkedAt -le [DateTimeOffset]::UtcNow.AddSeconds(1) -and ([DateTimeOffset]::UtcNow - $checkedAt).TotalSeconds -le [Math]::Max(30, 3 * $local.healthPeriodSeconds)
            if ($Mode -eq 'Prepare') { $fresh = $fresh -and $checkedAt -ge $started }
            $valid = $fresh -and $local.hostKind -eq $selected -and $local.controllerState -eq 'Monitoring' -and $local.ownsController -and $local.monitoringEnabled -and $local.permissionsOk -and $local.postgreSqlRole -in @('Primary','Standby') -and $local.settings.serviceName -ieq $profile.ServiceName -and (Test-SamePath $local.settings.binDirectory $profile.BinDirectory) -and (Test-SamePath $local.settings.dataDirectory $profile.DataDirectory) -and $local.settings.port -eq $profile.Port -and $null -ne $local.service -and $local.service.managedByService -and $local.service.serviceState -eq 'Running' -and $local.service.computer -ieq $env:COMPUTERNAME -and $local.service.serviceName -ieq $profile.ServiceName -and (Test-SamePath $local.service.dataDirectory $profile.DataDirectory) -and $local.service.port -eq $profile.Port -and $local.service.serviceProcessId -gt 0 -and $local.service.postmasterProcessId -gt 0 -and $local.service.listenerProcessId -eq $local.service.postmasterProcessId
        }

        if ($valid) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Fresh controller readiness was not confirmed. Inspect service state, permissions and the local snapshot in this report.' }
        Start-Sleep -Seconds 2
    } while ($true)

    $report.Ready = $true
    $report.Message = 'Controller ownership, PostgreSQL service and fresh local status confirmed.'
    Write-Host $report.Message -ForegroundColor Green
}
catch {
    $exitCode = 1
    $report.Message = $_.Exception.Message
    Write-Host ('FAILED: ' + $report.Message) -ForegroundColor Red
}
finally {
    for ($index = $locks.Count - 1; $index -ge 0; $index--) { $locks[$index].ReleaseMutex(); $locks[$index].Dispose() }
    if ($transcript) { Stop-Transcript | Out-Null }
    $report.CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $null = New-Item -ItemType Directory -Path $run -Force
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath ($resultPath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($resultPath + '.tmp') -Destination $resultPath -Force
}

Write-Host ('Result: ' + $resultPath)
if (!$NoPause) { try { $null = Read-Host 'Press ENTER to close' } catch {} }
exit $exitCode
