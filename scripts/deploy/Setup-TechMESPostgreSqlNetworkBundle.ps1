#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:EmbeddedNetworkSetup = ''
$lock = $null
$mutexes = New-Object 'System.Collections.Generic.List[Threading.Mutex]'
$errorFile = Join-Path $PSScriptRoot 'error.txt'

function Assert-NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Reparse points are not allowed in the PostgreSQL command store.'
        }
        $current = Split-Path -Path $current -Parent
    }
}

try {
    $setup = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:EmbeddedNetworkSetup)) | ConvertFrom-Json
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run this setup locally in PowerShell as Administrator.'
        }
    } finally { $identity.Dispose() }

    $mode = [string]$setup.Mode
    $node = [string]$setup.NodeAddress
    $address = if ($mode -eq 'PrepareMaintenance') { $setup.MaintenanceAddress } else { $node }
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
    if ($address -notin $addresses) {
        throw ('Run this bundle on computer ' + $address)
    }

    $networkTool = Join-Path $PSScriptRoot 'Tools\Configure-TechMESPostgreSqlNetwork.ps1'
    $maintenanceTool = Join-Path $PSScriptRoot 'Tools\Configure-TechMESPostgreSqlMaintenance.ps1'
    $publicFile = Join-Path $PSScriptRoot 'public.cer'
    $resultFile = Join-Path $PSScriptRoot 'result.json'
    if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile -Force }
    if (Test-Path -LiteralPath $errorFile) { Remove-Item -LiteralPath $errorFile -Force }

    if ($mode -ne 'PrepareMaintenance') {
        if ($setup.ComputerName -ine $env:COMPUTERNAME -and $setup.ComputerName -notin $addresses) {
            throw 'This profile belongs to a different PostgreSQL computer.'
        }
        $runtime = Get-Service -Name 'TechMES.Runtime.Service' -ErrorAction SilentlyContinue
        $hostService = if ($null -ne $runtime) { 'TechMES.Runtime.Service' } else { 'TechMES.PostgreSql.Agent' }
        if (($setup.HostMode -eq 'Runtime' -and $null -eq $runtime) -or ($setup.HostMode -eq 'Standalone' -and $null -ne $runtime)) {
            throw 'The installed controller differs from the selected PostgreSQL profile.'
        }
        $null = Get-Service -Name $hostService -ErrorAction Stop
        $commandsRoot = Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\' + $setup.ServiceName.ToUpperInvariant() + '\Commands')
        $networkRoot = Join-Path $commandsRoot 'Network'
        foreach ($name in @('Global\TechMES.ServerUpdate', ('Global\TechMES.PostgreSqlSetup.' + $setup.ServiceName))) {
            $mutex = New-Object Threading.Mutex($false, $name)
            try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
            if (!$locked) { $mutex.Dispose(); throw 'Another deployment or PostgreSQL setup is running.' }
            $mutexes.Add($mutex)
        }
        Assert-NoLinks (Join-Path $networkRoot 'console.lock')
        if (Test-Path -LiteralPath $networkRoot -PathType Container) {
            $lock = [IO.File]::Open((Join-Path $networkRoot 'console.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            if ((Test-Path -LiteralPath (Join-Path $networkRoot 'console-active.json')) -or (Test-Path -LiteralPath (Join-Path $networkRoot 'active-switch.json'))) {
                throw 'Finish the active PostgreSQL switch before configuring its controller.'
            }
        }
    }

    switch ($mode) {
        'PrepareNodes' {
            $answer = & $networkTool -Mode Prepare -NodeAddress $node -PeerAddress $setup.PeerAddress -ExpectedSystemIdentifier $setup.SystemIdentifier -ExpectedServiceName $setup.ServiceName -Port $setup.Port -AsObject
            $credentialPath = Join-Path $commandsRoot 'credentials.bin'
            Assert-NoLinks $credentialPath
            if (!(Test-Path -LiteralPath $credentialPath -PathType Leaf)) {
                # Read credentials in the PowerShell window that is running this bundle.
                $credentialTool = Join-Path $PSScriptRoot 'Tools\Invoke-TechMESPostgreSqlCommand.ps1'
                & $credentialTool -Mode Credentials -ServiceName $setup.ServiceName -ConsoleCredentialPrompt
            } else {
                Write-Host 'Using the existing local PostgreSQL command credentials.'
            }
            Copy-Item -LiteralPath $answer.PublicCertificate -Destination $publicFile -Force
        }
        'ConnectNodes' {
            if (!(Test-Path -LiteralPath (Join-Path $commandsRoot 'credentials.bin') -PathType Leaf)) {
                throw 'Complete local node preparation and enter the command credentials first.'
            }
            $answer = & $networkTool -Mode Connect -NodeAddress $node -PeerAddress $setup.PeerAddress -ExpectedSystemIdentifier $setup.SystemIdentifier -ExpectedServiceName $setup.ServiceName -Port $setup.Port -PeerCertificatePath (Join-Path $PSScriptRoot 'peer.cer') -ExpectedPeerSha256 $setup.PeerSha256 -AsObject
        }
        'PrepareMaintenance' {
            $answer = & $maintenanceTool -Mode Prepare -MaintenanceAddress $setup.MaintenanceAddress -NodeAddress $node -PeerAddress $setup.PeerAddress -NodeSha256 $setup.NodeSha256 -PeerSha256 $setup.PeerSha256 -SystemIdentifier $setup.SystemIdentifier -Port $setup.Port -AsObject
            Copy-Item -LiteralPath $answer.PublicCertificate -Destination $publicFile -Force
        }
        'TrustMaintenance' {
            $answer = & $maintenanceTool -Mode Trust -MaintenanceAddress $setup.MaintenanceAddress -HostService $hostService -NodeAddress $node -PeerAddress $setup.PeerAddress -SystemIdentifier $setup.SystemIdentifier -ExpectedServiceName $setup.ServiceName -Port $setup.Port -CertificatePath (Join-Path $PSScriptRoot 'maintenance.cer') -ExpectedMaintenanceSha256 $setup.MaintenanceSha256 -AsObject
        }
        default { throw 'Unknown PostgreSQL network setup action.' }
    }

    $answer | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultFile -Encoding UTF8
    $answer | Format-List
    if ($mode -in @('PrepareNodes','PrepareMaintenance')) { Write-Host ('Public certificate to copy: ' + $publicFile) }
    Write-Host ('Result: ' + $resultFile)
} catch {
    Set-Content -LiteralPath $errorFile -Value $_.Exception.Message -Encoding UTF8 -ErrorAction SilentlyContinue
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    for ($index = $mutexes.Count - 1; $index -ge 0; $index--) {
        $mutexes[$index].ReleaseMutex()
        $mutexes[$index].Dispose()
    }
}
