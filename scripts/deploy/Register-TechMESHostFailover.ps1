#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+\\[A-Za-z0-9_.-]+\$$')]
    [string]$PeerComputerAccount
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ((Get-CimInstance Win32_ComputerSystem).DomainRole -ne 3) {
    throw 'Install the endpoint only on the TechMES member server, not on a domain controller.'
}
if ((Get-Service WinRM).Status -ne 'Running') {
    throw 'An existing domain-authenticated Windows Remote Management listener is required.'
}
$null = ([Security.Principal.NTAccount]::new($PeerComputerAccount)).Translate([Security.Principal.SecurityIdentifier])

$modulePath = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules\TechMES.HostFailover'
$roles = Join-Path $modulePath 'RoleCapabilities'
foreach ($path in @($modulePath, $roles)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The endpoint module cannot be installed through a reparse point.'
    }
}
$null = New-Item -Path $roles -ItemType Directory -Force
$modulescript = Join-Path $modulePath 'TechMES.HostFailover.psm1'
$manifest = Join-Path $modulePath 'TechMES.HostFailover.psd1'
$capability = Join-Path $roles 'TechMES.HostFailover.psrc'
$configuration = Join-Path $modulePath 'TechMES.HostFailover.pssc'

@'
function Invoke-TechMESHostVipHandoff {
    [CmdletBinding()]
    param()

    $ErrorActionPreference = 'Stop'
    $service = Get-CimInstance Win32_Service -Filter "Name='TechMES.Host'"
    if ($null -eq $service) { throw 'TechMES.Host is not installed.' }

    if ($service.PathName -match '^\s*"([^"]+)"\s*$' -or $service.PathName -match '^\s*(\S+)\s*$') {
        $exe = $Matches[1]
    }
    else { throw 'Unexpected TechMES.Host service executable.' }
    if (![IO.Path]::IsPathRooted($exe) -or [IO.Path]::GetFileName($exe) -ine 'TechMES.Host.exe' -or !(Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The registered apphost does not exist or does not match TechMES.Host.exe.'
    }

    $restoreService = $service.State -eq 'Running'
    if ($restoreService) {
        $settingsPath = Join-Path (Split-Path -Parent $exe) 'appsettings.json'
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $port = [int]$settings.HostService.Port
        if ($port -lt 1 -or $port -gt 65535) { throw 'The local Host liveness port is invalid.' }

        $health = $null
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/health" -TimeoutSec 3 -UseBasicParsing
            $health = $response.Content | ConvertFrom-Json
        }
        catch {
            Write-Verbose 'The local Host liveness endpoint did not confirm readiness.'
        }
        if ($null -ne $health -and $health.Service -eq 'TechMES.Host' -and $health.Status -eq 'OK') {
            throw 'TechMES.Host on the preferred node is healthy. Handoff is blocked.'
        }

        & "$env:SystemRoot\System32\sc.exe" stop TechMES.Host | Out-Null
    }

    $controller = Get-Service -Name 'TechMES.Host'
    $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(40))
    $controller.Refresh()
    if ($controller.Status -ne 'Stopped') { throw 'Windows did not confirm the old Host stopped.' }

    try {
        # The apphost takes the normal exclusive VIP journal lock and uses the existing mTLS handoff.
        & $exe '--handoff-host-service'
        if ($LASTEXITCODE -ne 0) { throw "The Host VIP handoff failed (exit $LASTEXITCODE)." }
    }
    finally {
        if ($restoreService) { Start-Service -Name 'TechMES.Host' }
    }
}

function Start-TechMESPreferredHost {
    [CmdletBinding()]
    param()

    $ErrorActionPreference = 'Stop'
    $controller = Get-Service -Name 'TechMES.Host'
    if ($controller.Status -eq 'Stopped') { $controller.Start() }
    $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(40))
}

Export-ModuleMember -Function Invoke-TechMESHostVipHandoff, Start-TechMESPreferredHost
'@ | Set-Content -LiteralPath $modulescript -Encoding UTF8

$recoveryCommands = @('Invoke-TechMESHostVipHandoff', 'Start-TechMESPreferredHost')
New-ModuleManifest -Path $manifest -RootModule 'TechMES.HostFailover.psm1' -ModuleVersion '1.0.0' -FunctionsToExport $recoveryCommands
New-PSRoleCapabilityFile -Path $capability -ModulesToImport @('TechMES.HostFailover') -VisibleFunctions $recoveryCommands
New-PSSessionConfigurationFile -Path $configuration -SessionType RestrictedRemoteServer -RunAsVirtualAccount -RoleDefinitions @{
    $PeerComputerAccount = @{ RoleCapabilities = 'TechMES.HostFailover' }
}
if (Get-PSSessionConfiguration -Name 'TechMES.HostFailover' -ErrorAction SilentlyContinue) {
    Set-PSSessionConfiguration -Name 'TechMES.HostFailover' -Path $configuration -Force
}
else {
    Register-PSSessionConfiguration -Name 'TechMES.HostFailover' -Path $configuration -Force
}
