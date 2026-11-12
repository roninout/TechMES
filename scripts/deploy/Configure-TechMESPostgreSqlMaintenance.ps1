#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Prepare','Trust')][string]$Mode='Prepare', [string]$MaintenanceAddress='192.168.1.118', [string]$CertificatePath='', [ValidatePattern('^$|^[0-9A-Fa-f]{64}$')][string]$ExpectedMaintenanceSha256='', [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$Node131Sha256='B6D245E6B30C1B821E9E67B3788D0DC054211ACDF8EC403EC100DAFCA677E341', [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$Node132Sha256='FEAC30D8179603A5042B717B1BE332825586D8C288D6151C1F89ACF13615EF31', [ValidatePattern('^[0-9]{1,20}$')][string]$SystemIdentifier='7703468038317098096', [ValidateRange(1024,65535)][int]$Port=5115, [ValidateSet('','TechMES.Runtime.Service','TechMES.PostgreSql.Agent')][string]$HostService='', [switch]$RestoreNetwork, [ValidateSet('','192.168.1.131','192.168.1.132')][string]$NodeAddress='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# This file uses ASCII only for Windows PowerShell 5.1.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run locally on Windows as Administrator.'
}

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run locally as Administrator.'
    }
}
finally {
    $identity.Dispose()
}

# Rejects links in every existing part of a configuration path.
function Assert-NoLinks([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)

    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point: $current"
        }

        $current=Split-Path -Path $current -Parent
    }
}

# Computes the certificate pin on .NET Framework.
function Get-CertificatePin([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha=[Security.Cryptography.SHA256]::Create()

    try {
        return [BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)).Replace('-','')
    }
    finally {
        $sha.Dispose()
    }
}

# Tests a certificate purpose by OID without localized display names.
function Test-CertificatePurpose([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$Oid) {
    $purposes=@(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
            ForEach-Object {
                ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages
            } |
            Where-Object { $_.Value -eq $Oid }
    )

    return $purposes.Count -eq 1
}

# Sets a fresh DACL, not just an owner; old explicit ACEs are removed.
function Set-PrivateAcl([string]$Path, [bool]$Directory) {
    Assert-NoLinks $Path

    $security=if ($Directory) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else {
        New-Object Security.AccessControl.FileSecurity
    }

    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))

    $inheritance=if ($Directory) {
        'ContainerInherit,ObjectInherit'
    }
    else {
        'None'
    }

    foreach ($account in @('S-1-5-18','S-1-5-32-544')) {
        $sid=New-Object Security.Principal.SecurityIdentifier($account)

        $security.AddAccessRule(
            (New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl',$inheritance,'None','Allow'))
        )
    }

    Set-Acl -LiteralPath $Path -AclObject $security
}

# Matches the private-file checks used by PostgreSqlCommandStore.Read.
function Assert-PrivateFile([string]$Path) {
    Assert-NoLinks $Path

    $acl=Get-Acl -LiteralPath $Path

    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) {
        throw "Unexpected owner: $Path"
    }

    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) {
            throw "Unexpected Allow rule: $Path"
        }
    }
}

# Preserves the previous JSON; a private destination stays private after Replace.
function Write-Settings([string]$Path, $Value, [bool]$Private=$false) {
    Assert-NoLinks $Path

    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $backup=$Path+'.before-maintenance-'+[guid]::NewGuid().ToString('N')+'.bak'

    [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($true)))

    if ($Private) {
        Set-PrivateAcl $temporary $false

        if ([IO.File]::Exists($Path)) {
            Set-PrivateAcl $Path $false
        }
    }

    if ([IO.File]::Exists($Path)) {
        [IO.File]::Replace($temporary,$Path,$backup)
    }
    else {
        [IO.File]::Move($temporary,$Path)
    }

    if ($Private) {
        Set-PrivateAcl $Path $false

        if ([IO.File]::Exists($backup)) {
            Set-PrivateAcl $backup $false
        }

        Assert-PrivateFile $Path
    }
}

# Extracts only a standard TechMES service executable.
function Get-HostDirectory($Service) {
    if ($Service.PathName -match '^\s*"([^"]+)"') {
        $executable=$matches[1]
    }
    elseif ($Service.PathName -match '^\s*(\S+\.exe)(?:\s|$)') {
        $executable=$matches[1]
    }
    else {
        throw 'Cannot read the controller executable.'
    }

    if ([IO.Path]::GetFileName($executable) -ine ($Service.Name+'.exe')) {
        throw 'Use the standard TechMES apphost executable.'
    }

    $directory=[IO.Path]::GetDirectoryName($executable)

    Assert-NoLinks $directory

    return $directory
}

# Uses the explicitly selected host; automatic selection follows the Tools location.
function Select-HostService($Services, [string]$Requested, [string]$ToolDirectory) {
    $available=@($Services)

    if ($Requested) {
        $selected=@($available | Where-Object { $_.Name -eq $Requested })

        if ($selected.Count -ne 1) {
            throw "The requested host service is not installed: $Requested"
        }

        return $selected[0]
    }

    if ($available.Count -eq 1) {
        return $available[0]
    }

    $matchesLocation=@(
        $available | Where-Object {
            (Get-HostDirectory $_) -ieq $ToolDirectory
        }
    )

    if ($matchesLocation.Count -eq 1) {
        return $matchesLocation[0]
    }

    throw 'Specify -HostService TechMES.Runtime.Service or -HostService TechMES.PostgreSql.Agent.'
}

# A host must not restart while a switch or command is unfinished.
function Assert-NoPendingCommands([string]$CommandsRoot) {
    Assert-NoLinks $CommandsRoot

    if (Test-Path -LiteralPath (Join-Path $CommandsRoot 'Network\active-switch.json')) {
        throw 'Finish the reserved switch before restarting its controller.'
    }

    if (!(Test-Path -LiteralPath $CommandsRoot -PathType Container)) {
        return
    }

    foreach ($folder in Get-ChildItem -LiteralPath $CommandsRoot -Directory | Where-Object { $_.Name -match '^[0-9a-f]{32}$' }) {
        $requestPath=Join-Path $folder.FullName 'request.json'
        $journalPath=Join-Path $folder.FullName 'journal.json'

        if (!(Test-Path -LiteralPath $requestPath)) {
            continue
        }

        if (!(Test-Path -LiteralPath $journalPath)) {
            throw 'A queued PostgreSQL command must finish first.'
        }

        $journal=Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($journal.state -notin @('Succeeded','Rejected')) {
            throw 'Inspect the outstanding PostgreSQL command before restarting the controller.'
        }
    }
}

$address=$null

if (![Net.IPAddress]::TryParse($MaintenanceAddress,[ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($address)) {
    throw 'Specify the IPv4 address of the Maintenance computer.'
}

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)

if ($Mode -eq 'Prepare') {
    if ($MaintenanceAddress -notin $addresses) {
        throw 'Run Prepare on the Maintenance computer.'
    }

    if ($Node131Sha256 -ieq $Node132Sha256) {
        throw 'The database server certificate pins must be different.'
    }

    $root=Join-Path $env:ProgramData 'TechMES\PostgreSqlMaintenance'

    Assert-NoLinks $root

    if (!(Test-Path -LiteralPath $root -PathType Container)) {
        $null=New-Item -ItemType Directory -Path $root -Force
    }

    Set-PrivateAcl $root $true

    $settingsPath=Join-Path $root 'client.json'

    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        Assert-NoLinks $settingsPath

        $previous=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json

        $certificate=@(
            Get-ChildItem Cert:\LocalMachine\My |
                Where-Object {
                    (Get-CertificatePin $_) -ieq $previous.ClientCertificateSha256
                }
        ) | Select-Object -First 1

        if ($null -eq $certificate -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) {
            throw 'The existing Maintenance certificate is missing or expired. Do not replace it automatically.'
        }
    }
    else {
        if ($ExpectedMaintenanceSha256) {
            throw 'Existing client.json is missing; the requested certificate must not be rotated automatically.'
        }

        $certificate=New-SelfSignedCertificate -Type Custom -Subject ('CN=TechMES PostgreSQL Maintenance '+$env:COMPUTERNAME) -FriendlyName 'TechMES PostgreSQL Maintenance' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2') -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter ((Get-Date).AddYears(2))
    }

    $pin=Get-CertificatePin $certificate

    if (($ExpectedMaintenanceSha256 -and $pin -ine $ExpectedMaintenanceSha256) -or !(Test-CertificatePurpose $certificate '1.3.6.1.5.5.7.3.2')) {
        throw 'The Maintenance certificate differs or has no client purpose.'
    }

    $connection=[ordered]@{
        SystemIdentifier=$SystemIdentifier
        ClientCertificateSha256=$pin
        Node131CertificateSha256=$Node131Sha256.ToUpperInvariant()
        Node132CertificateSha256=$Node132Sha256.ToUpperInvariant()
        Port=$Port
    }

    Write-Settings $settingsPath $connection $true

    $publicPath=Join-Path $root 'TechMES-PostgreSql-Maintenance.cer'

    $null=Export-Certificate -Cert $certificate -FilePath $publicPath -Type CERT -Force

    [pscustomobject]@{
        Computer=$env:COMPUTERNAME
        MaintenanceAddress=$MaintenanceAddress
        MaintenanceSha256=$pin
        PublicCertificate=$publicPath
        Settings=$settingsPath
        PrivateAcl=$true
    } | Format-List

    return
}

if (!$CertificatePath -or !$ExpectedMaintenanceSha256) {
    throw 'Specify CertificatePath and the MaintenanceSha256 printed by Prepare.'
}

$services=@(Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service' OR Name='TechMES.PostgreSql.Agent'")

$service=Select-HostService $services $HostService (Split-Path -Path $PSScriptRoot -Parent)
$settingsPath=Join-Path (Get-HostDirectory $service) 'appsettings.PostgreSqlControl.json'

Assert-NoLinks $settingsPath

$settings=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$controlProperty=$settings.PSObject.Properties['PostgreSqlControl']

if ($null -eq $controlProperty -or $null -eq $controlProperty.Value) {
    throw "PostgreSqlControl is missing: $settingsPath"
}

$control=$controlProperty.Value

foreach ($name in @('CommandsEnabled','ExpectedSystemIdentifier','ServiceName','Port')) {
    if ($null -eq $control.PSObject.Properties[$name]) {
        throw "PostgreSqlControl.$name is missing: $settingsPath"
    }
}

if (!$control.CommandsEnabled -or $control.ExpectedSystemIdentifier -ne $SystemIdentifier -or $control.ServiceName -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'The selected host must have commands enabled and the expected cluster SID.'
}

$commandsRoot=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$control.ServiceName.ToUpperInvariant()+'\Commands')

Assert-NoPendingCommands $commandsRoot

$networkProperty=$settings.PSObject.Properties['PostgreSqlNetwork']
$restored=$false

if ($null -eq $networkProperty -or $null -eq $networkProperty.Value) {
    if (!$RestoreNetwork) {
        throw "PostgreSqlNetwork is missing in $settingsPath. Use explicit -RestoreNetwork -NodeAddress only with the previously verified node pins."
    }

    if (!$NodeAddress -or $NodeAddress -notin $addresses) {
        throw 'RestoreNetwork requires the local database NodeAddress.'
    }

    $peerAddress=if ($NodeAddress -eq '192.168.1.131') {
        '192.168.1.132'
    }
    else {
        '192.168.1.131'
    }

    $localPin=if ($NodeAddress -eq '192.168.1.131') {
        $Node131Sha256
    }
    else {
        $Node132Sha256
    }

    $peerPin=if ($NodeAddress -eq '192.168.1.131') {
        $Node132Sha256
    }
    else {
        $Node131Sha256
    }

    if ($localPin -ieq $peerPin) {
        throw 'Local and peer certificate pins must differ.'
    }

    $network=[pscustomobject]@{
        Enabled=$true
        NodeAddress=$NodeAddress
        PeerAddress=$peerAddress
        Port=$Port
        CertificateSha256=$localPin.ToUpperInvariant()
        PeerCertificateSha256=$peerPin.ToUpperInvariant()
    }

    $restored=$true
}
else {
    $network=$networkProperty.Value
}

foreach ($name in @('Enabled','NodeAddress','PeerAddress','Port','CertificateSha256','PeerCertificateSha256')) {
    if ($null -eq $network.PSObject.Properties[$name]) {
        throw "PostgreSqlNetwork.$name is missing: $settingsPath"
    }
}

if (!$network.Enabled -or $network.NodeAddress -notin $addresses -or $network.NodeAddress -notin @('192.168.1.131','192.168.1.132') -or $network.PeerAddress -notin @('192.168.1.131','192.168.1.132') -or $network.NodeAddress -eq $network.PeerAddress -or $network.Port -lt 1024 -or $network.Port -gt 65535 -or $network.Port -eq $control.Port -or $network.CertificateSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $network.PeerCertificateSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $network.CertificateSha256 -ieq $network.PeerCertificateSha256) {
    throw "Invalid database network profile: $settingsPath"
}

if ($NodeAddress -and $network.NodeAddress -ne $NodeAddress) {
    throw 'The selected host profile belongs to another node.'
}

# A missing profile can reuse only its original certificate and private key.
$localCertificate=@(
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object {
            (Get-CertificatePin $_) -ieq $network.CertificateSha256
        }
) | Select-Object -First 1

if ($null -eq $localCertificate -or !$localCertificate.HasPrivateKey -or $localCertificate.NotBefore -gt (Get-Date) -or $localCertificate.NotAfter -le (Get-Date) -or !(Test-CertificatePurpose $localCertificate '1.3.6.1.5.5.7.3.1') -or !(Test-CertificatePurpose $localCertificate '1.3.6.1.5.5.7.3.2')) {
    throw 'The original node certificate/private key is missing, expired or has unexpected purposes. No node certificate was created.'
}

Assert-NoLinks $CertificatePath

$certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2([IO.Path]::GetFullPath($CertificatePath))

try {
    $pin=Get-CertificatePin $certificate

    if ($pin -ine $ExpectedMaintenanceSha256 -or $pin -ieq $network.CertificateSha256 -or $pin -ieq $network.PeerCertificateSha256 -or $certificate.Subject -notlike 'CN=TechMES PostgreSQL Maintenance *' -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or !(Test-CertificatePurpose $certificate '1.3.6.1.5.5.7.3.2')) {
        throw 'The Maintenance certificate pin, validity or client purpose differs.'
    }
}
finally {
    $certificate.Dispose()
}

$network | Add-Member -NotePropertyName MaintenanceCertificateSha256 -NotePropertyValue $pin -Force
$settings | Add-Member -NotePropertyName PostgreSqlNetwork -NotePropertyValue $network -Force

Write-Settings $settingsPath $settings

$ruleName='TechMES PostgreSQL Maintenance '+$network.NodeAddress

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$null=New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $network.Port -LocalAddress $network.NodeAddress -RemoteAddress $MaintenanceAddress -Profile Any

if ($restored) {
    $peerRule='TechMES PostgreSQL mTLS '+$network.NodeAddress

    Get-NetFirewallRule -DisplayName $peerRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    $null=New-NetFirewallRule -DisplayName $peerRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $network.Port -LocalAddress $network.NodeAddress -RemoteAddress @($network.NodeAddress,$network.PeerAddress) -Profile Any
}

Restart-Service -Name $service.Name -ErrorAction Stop

[pscustomobject]@{
    Node=$network.NodeAddress
    HostService=$service.Name
    MaintenanceAddress=$MaintenanceAddress
    MaintenanceSha256=$pin
    Settings=$settingsPath
    NetworkRestored=$restored
} | Format-List