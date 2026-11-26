#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Check','Prepare','Failback','Failover')][string]$Mode = 'Check', [Guid]$RunId = [Guid]::NewGuid(), [switch]$NoPause)

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
$coordinatorLock = $null
$settingsBackup = $null
$settingsPath = $null
$report = [ordered]@{ Format = 1; ProfileFingerprint = $payload.ProfileFingerprint; RunId = $RunId.ToString(); Computer = $env:COMPUTERNAME; Addresses = @(); CheckedAtUtc = ''; Ready = $false; PreparationCompleted = $false; HostKind = ''; HostService = ''; HostServiceState = ''; HostProcessId = 0; SettingsPath = ''; Local = $null; Failback = $null; Failover = $null; FailoverEnabled = $null; Message = ''; LogPath = $logPath }
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

function Get-FailbackSetting($Policy, $Settings) {
    $network = $Settings.PostgreSqlNetwork
    if ($Policy.PreferredPrimary -notin @($network.NodeAddress,$network.PeerAddress) -or ($Policy.Enabled -and $Policy.PreferredPrimary -ne $network.PeerAddress)) { throw 'The selected failback policy does not match the local PostgreSQL pair.' }

    $current = $Settings.PSObject.Properties['PostgreSqlAutomaticFailback']
    if (!$Policy.Enabled -and ($null -eq $current -or $null -eq $current.Value -or !$current.Value.PSObject.Properties['Enabled'] -or !$current.Value.Enabled)) { return $null }
    $value = if ($null -eq $current -or $null -eq $current.Value) { [pscustomobject]@{} } else { $current.Value }
    $value | Add-Member -NotePropertyName 'Enabled' -NotePropertyValue ([bool]$Policy.Enabled) -Force
    $value | Add-Member -NotePropertyName 'PreferredPrimary' -NotePropertyValue ([string]$Policy.PreferredPrimary) -Force
    return $value
}

function Get-FailoverSetting($Policy, $Settings) {
    $current = $Settings.PSObject.Properties['PostgreSqlAutomaticFailover']
    $value = if ($null -eq $current -or $null -eq $current.Value) { [pscustomobject]@{} } else { $current.Value }
    if ($value -isnot [pscustomobject]) { throw 'PostgreSqlAutomaticFailover must be a JSON object.' }

    $thresholdSetting = $value.PSObject.Properties['FailureThreshold']
    $threshold = 0
    if ($Policy.Enabled -and $null -ne $thresholdSetting -and
        (![int]::TryParse([string]$thresholdSetting.Value, [ref]$threshold) -or $threshold -lt 1 -or $threshold -gt 10)) {
        throw 'PostgreSqlAutomaticFailover.FailureThreshold must be an integer from 1 to 10.'
    }

    $value | Add-Member -NotePropertyName 'Enabled' -NotePropertyValue ([bool]$Policy.Enabled) -Force
    return $value
}

function Read-FailoverEnabled([string]$Path) {
    $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $section = $settings.PSObject.Properties['PostgreSqlAutomaticFailover']
    if ($null -eq $section -or $null -eq $section.Value) { return }
    $enabled = $section.Value.PSObject.Properties['Enabled']
    if ($null -ne $enabled -and $enabled.Value -is [bool]) { return $enabled.Value }
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
    $waitForFresh = $Mode -eq 'Prepare'

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
    if ($Mode -in @('Failback','Failover')) {
        $policy = $payload.PSObject.Properties[$Mode].Value
        if ($null -eq $policy -or $policy.SystemIdentifier -notmatch '^[0-9]{1,20}$' -or $addresses -notcontains $policy.NodeAddress -or $policy.Enabled -isnot [bool]) { throw 'Invalid policy or this policy belongs to another computer or cluster.' }
        $selectedService = if ($selected -eq 'Runtime') { $runtime } else { $agent }
        if ($null -eq $selectedService -or $selectedService.State -ne 'Running') { throw 'Start the configured controller before applying the PostgreSQL policy.' }
        $hostDirectory = Get-ServiceDirectory $selectedService.PathName ($report.HostService + '.exe')
        $settingsPath = Join-Path $hostDirectory 'appsettings.PostgreSqlControl.json'
        $commandsDirectory = Join-Path (Split-Path -Parent $statusPath) 'Commands'
        $credentialsPath = Join-Path $commandsDirectory 'credentials.bin'
        $networkDirectory = Join-Path $commandsDirectory 'Network'
        $lockPath = Join-Path $networkDirectory 'console.lock'

        foreach ($path in @($settingsPath, $lockPath, $credentialsPath)) {
            for ($current = $path; $current; $current = Split-Path -Path $current -Parent) {
                if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Controller paths must not contain reparse points.' }
            }
        }
        if (!(Test-Path -LiteralPath $commandsDirectory -PathType Container) -or ($policy.Enabled -and !(Test-Path -LiteralPath $credentialsPath -PathType Leaf))) { throw 'Configure local PostgreSQL command credentials before applying the policy.' }
        $null = New-Item -ItemType Directory -Path $networkDirectory -Force
        $coordinatorLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        if ((Test-Path -LiteralPath (Join-Path $networkDirectory 'active-switch.json')) -or (Test-Path -LiteralPath (Join-Path $networkDirectory 'console-active.json'))) { throw 'Complete the PostgreSQL operation before changing automatic switching settings.' }

        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $network = $settings.PostgreSqlNetwork
        $control = $settings.PostgreSqlControl
        if ($control.ServiceName -ine $profile.ServiceName -or $control.Port -ne $profile.Port -or !(Test-SamePath $control.BinDirectory $profile.BinDirectory) -or !(Test-SamePath $control.DataDirectory $profile.DataDirectory)) { throw 'The installed PostgreSQL profile differs from the selected profile.' }
        if (!$network.Enabled -or !$control.CommandsEnabled -or [string]$control.ExpectedSystemIdentifier -cne $policy.SystemIdentifier -or $network.NodeAddress -cne $policy.NodeAddress -or $network.PeerAddress -cne $policy.PeerAddress -or $network.NodeAddress -eq $network.PeerAddress) { throw 'The local controller is not configured for this pair and cluster.' }
        $before = $settings | ConvertTo-Json -Depth 32
        if ($Mode -eq 'Failback') {
            $value = Get-FailbackSetting $policy $settings
            if ($null -ne $value) { $settings | Add-Member -NotePropertyName 'PostgreSqlAutomaticFailback' -NotePropertyValue $value -Force }
        }
        else {
            $settings | Add-Member -NotePropertyName 'PostgreSqlAutomaticFailover' -NotePropertyValue (Get-FailoverSetting $policy $settings) -Force
        }
        $after = $settings | ConvertTo-Json -Depth 32
        if ($after -cne $before) {
            $temporary = $settingsPath + '.' + $RunId.ToString('N') + '.tmp'
            $backup = $settingsPath + '.' + $RunId.ToString('N') + '.bak'
            [IO.File]::WriteAllText($temporary, $after, (New-Object Text.UTF8Encoding($true)))
            [IO.File]::Replace($temporary, $settingsPath, $backup)
            $settingsBackup = $backup
        }
        if ($Mode -eq 'Failover' -and !$settingsBackup) {
            $hostProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $selectedService.ProcessId)
            $waitForFresh = $null -ne $hostProcess -and (Get-Item -LiteralPath $settingsPath).LastWriteTimeUtc -gt $hostProcess.CreationDate.ToUniversalTime()
        }
        if ($settingsBackup -or $waitForFresh) {
            Restart-Service -Name $report.HostService
            $waitForFresh = $true
        }
        $report.PreparationCompleted = $true
        $report[$Mode] = $policy
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($(if ($waitForFresh) { [Math]::Max(90, $profile.HostHealthPeriodSeconds + 30) } else { 0 }))

    do {
        $hostService = Get-CimInstance Win32_Service -Filter ("Name='" + $report.HostService + "'")
        if ($null -eq $hostService) { throw 'The selected controller service is not installed.' }
        $report.HostServiceState = [string]$hostService.State
        $report.HostProcessId = [int]$hostService.ProcessId
        $hostDirectory = Get-ServiceDirectory $hostService.PathName ($report.HostService + '.exe')
        $report.SettingsPath = Join-Path $hostDirectory 'appsettings.PostgreSqlControl.json'
        # Runtime может читать параметры из основного appsettings, но сетевой мастер требует отдельный файл.
        if (!(Test-Path -LiteralPath $report.SettingsPath -PathType Leaf)) {
            throw ('Controller settings are missing: ' + $report.SettingsPath + '. Use Preparing the controller in Maintenance to configure the selected controller before network setup.')
        }
        $valid = $false

        if ($hostService.State -eq 'Running' -and $hostService.ProcessId -gt 0 -and (Test-Path -LiteralPath $statusPath)) {
            $local = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            $report.Local = $local
            $checkedAt = [DateTimeOffset]$local.checkedAtUtc
            $hostProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $hostService.ProcessId)
            $fresh = $null -ne $hostProcess -and $checkedAt.UtcDateTime -ge $hostProcess.CreationDate.ToUniversalTime() -and $checkedAt -le [DateTimeOffset]::UtcNow.AddSeconds(1) -and ([DateTimeOffset]::UtcNow - $checkedAt).TotalSeconds -le [Math]::Max(30, 3 * $local.healthPeriodSeconds)
            if ($waitForFresh) { $fresh = $fresh -and $checkedAt -ge $started }
            $valid = $fresh -and $local.hostKind -eq $selected -and $local.controllerState -eq 'Monitoring' -and $local.ownsController -and $local.monitoringEnabled -and $local.permissionsOk -and $local.postgreSqlRole -in @('Primary','Standby') -and $local.settings.serviceName -ieq $profile.ServiceName -and (Test-SamePath $local.settings.binDirectory $profile.BinDirectory) -and (Test-SamePath $local.settings.dataDirectory $profile.DataDirectory) -and $local.settings.port -eq $profile.Port -and $null -ne $local.service -and $local.service.managedByService -and $local.service.serviceState -eq 'Running' -and $local.service.computer -ieq $env:COMPUTERNAME -and $local.service.serviceName -ieq $profile.ServiceName -and (Test-SamePath $local.service.dataDirectory $profile.DataDirectory) -and $local.service.port -eq $profile.Port -and $local.service.serviceProcessId -gt 0 -and $local.service.postmasterProcessId -gt 0 -and $local.service.listenerProcessId -eq $local.service.postmasterProcessId
        }

        if ($valid) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Fresh controller readiness was not confirmed. Inspect service state, permissions and the local snapshot in this report.' }
        Start-Sleep -Seconds 2
    } while ($true)

    try { $report.FailoverEnabled = Read-FailoverEnabled $report.SettingsPath }
    catch {
        if ($Mode -eq 'Failover') { throw }
        Write-Verbose "Could not read the saved failover setting: $($_.Exception.Message)"
    }
    if ($Mode -eq 'Failover' -and ($null -eq $report.FailoverEnabled -or $report.FailoverEnabled -ne $policy.Enabled)) {
        throw 'The requested failover state was not confirmed in the installed controller settings.'
    }
    $report.Ready = $true
    $report.Message = switch ($Mode) {
        'Failback' { "Failback enabled: $($policy.Enabled). Preferred Primary: $($policy.PreferredPrimary)." }
        'Failover' { "Automatic PostgreSQL failover enabled: $($policy.Enabled) on $($policy.NodeAddress)." }
        default { 'Controller ownership, PostgreSQL service and fresh local status confirmed.' }
    }
    Write-Host $report.Message -ForegroundColor Green
}
catch {
    $exitCode = 1
    $report.Message = $_.Exception.Message
    if ($settingsBackup) {
        try {
            [IO.File]::Replace($settingsBackup, $settingsPath, ($settingsBackup + '.failed'))
            Restart-Service -Name $report.HostService
            $report.Message += ' Original controller settings restored.'
        } catch {
            $report.Message += " Rollback failed: $($_.Exception.Message)"
        }
    }
    Write-Host ('FAILED: ' + $report.Message) -ForegroundColor Red
}
finally {
    if ($null -ne $coordinatorLock) { $coordinatorLock.Dispose() }
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
