#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Prepare','Trust')][string]$Mode='Prepare', [string]$MaintenanceAddress='192.168.1.118', [string]$CertificatePath='', [ValidatePattern('^$|^[0-9A-Fa-f]{64}$')][string]$ExpectedMaintenanceSha256='', [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$Node131Sha256='B6D245E6B30C1B821E9E67B3788D0DC054211ACDF8EC403EC100DAFCA677E341', [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$Node132Sha256='FEAC30D8179603A5042B717B1BE332825586D8C288D6151C1F89ACF13615EF31', [ValidatePattern('^[0-9]{1,20}$')][string]$SystemIdentifier='7703468038317098096', [ValidateRange(1024,65535)][int]$Port=5115)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run locally on Windows as Administrator.' }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run locally as Administrator.' }
}
finally {
    $identity.Dispose()
}

$address=$null

if (![Net.IPAddress]::TryParse($MaintenanceAddress,[ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($address)) { throw 'Specify the IPv4 address of the Maintenance computer.' }

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)

# Вычисляет SHA-256 сертификата без новых API .NET Framework.
function Get-CertificatePin([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha=[Security.Cryptography.SHA256]::Create()

    try {
        return [BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)).Replace('-','')
    }
    finally {
        $sha.Dispose()
    }
}

# Сохраняет прежний JSON и атомарно устанавливает новый файл с непустым путём backup.
function Write-Settings([string]$Path, $Value) {
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $backup=$Path+'.before-maintenance-'+[guid]::NewGuid().ToString('N')+'.bak'

    [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($true)))

    if ([IO.File]::Exists($Path)) {
        [IO.File]::Replace($temporary,$Path,$backup)
    }
    else {
        [IO.File]::Move($temporary,$Path)
    }
}

if ($Mode -eq 'Prepare') {
    if ($MaintenanceAddress -notin $addresses) { throw 'Run Prepare on the Maintenance computer, not on a database node.' }
    if ($Node131Sha256 -ieq $Node132Sha256) { throw 'The database server certificate pins must be different.' }

    $root=Join-Path $env:ProgramData 'TechMES\PostgreSqlMaintenance'
    $current=[IO.Path]::GetFullPath($root)

    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed in the Maintenance settings path.' }

        $current=Split-Path -Path $current -Parent
    }

    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))

    foreach ($account in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($account)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }

    $directory=New-Object IO.DirectoryInfo($root)

    if (!$directory.Exists) {
        $directory.Create($acl)
    }
    else {
        Set-Acl -LiteralPath $root -AclObject $acl
    }

    $settingsPath=Join-Path $root 'client.json'
    $certificate=$null

    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $previous=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $certificate=@(Get-ChildItem Cert:\LocalMachine\My | Where-Object { (Get-CertificatePin $_) -ieq $previous.ClientCertificateSha256 }) | Select-Object -First 1

        if ($null -eq $certificate -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The configured Maintenance certificate is missing or expired. Inspect the existing configuration before replacing it.' }
    }
    else {
        $certificate=New-SelfSignedCertificate -Type Custom -Subject ('CN=TechMES PostgreSQL Maintenance '+$env:COMPUTERNAME) -FriendlyName 'TechMES PostgreSQL Maintenance' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2') -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter ((Get-Date).AddYears(2))
    }

    $pin=Get-CertificatePin $certificate
    $connection=[ordered]@{
        SystemIdentifier=$SystemIdentifier
        ClientCertificateSha256=$pin
        Node131CertificateSha256=$Node131Sha256.ToUpperInvariant()
        Node132CertificateSha256=$Node132Sha256.ToUpperInvariant()
        Port=$Port
    }

    Write-Settings $settingsPath $connection

    $settingsAcl=Get-Acl -LiteralPath $settingsPath
    $settingsAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $settingsPath -AclObject $settingsAcl

    $publicPath=Join-Path $root 'TechMES-PostgreSql-Maintenance.cer'
    $null=Export-Certificate -Cert $certificate -FilePath $publicPath -Type CERT -Force

    [pscustomobject]@{
        Computer=$env:COMPUTERNAME
        MaintenanceAddress=$MaintenanceAddress
        MaintenanceSha256=$pin
        PublicCertificate=$publicPath
        Settings=$settingsPath
    } | Format-List

    return
}

# Trust применяется к существующему контроллеру, сохраняя его ключ и настройки пары БД.
if (!$CertificatePath -or !$ExpectedMaintenanceSha256) { throw 'Specify CertificatePath and the MaintenanceSha256 printed by Prepare.' }

$runtime=Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service'"
$agent=Get-CimInstance Win32_Service -Filter "Name='TechMES.PostgreSql.Agent'"
$service=if ($null -ne $runtime) { $runtime } elseif ($null -ne $agent) { $agent } else { throw 'Install the PostgreSQL controller first.' }

if ($service.PathName -match '^\s*"([^"]+)"') {
    $executable=$matches[1]
}
elseif ($service.PathName -match '^\s*(\S+\.exe)(?:\s|$)') {
    $executable=$matches[1]
}
else {
    throw 'Cannot read the controller executable.'
}

if ([IO.Path]::GetFileName($executable) -ine ($service.Name+'.exe')) { throw 'Use the standard TechMES apphost executable.' }

$settingsPath=Join-Path ([IO.Path]::GetDirectoryName($executable)) 'appsettings.PostgreSqlControl.json'
$settings=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$network=$settings.PostgreSqlNetwork

if (!$network.Enabled -or $network.NodeAddress -notin $addresses -or $network.NodeAddress -notin @('192.168.1.131','192.168.1.132') -or $network.Port -lt 1024 -or $network.Port -gt 65535) { throw 'Configure the database node network first.' }

$commandsRoot=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$settings.PostgreSqlControl.ServiceName.ToUpperInvariant()+'\Commands')

if (Test-Path -LiteralPath (Join-Path $commandsRoot 'Network\active-switch.json')) { throw 'Finish the reserved switch before restarting its controller.' }

if (Test-Path -LiteralPath $commandsRoot -PathType Container) {
    foreach ($folder in Get-ChildItem -LiteralPath $commandsRoot -Directory | Where-Object { $_.Name -match '^[0-9a-f]{32}$' }) {
        $requestPath=Join-Path $folder.FullName 'request.json'
        $journalPath=Join-Path $folder.FullName 'journal.json'

        if (!(Test-Path -LiteralPath $requestPath)) { continue }
        if (!(Test-Path -LiteralPath $journalPath)) { throw 'A queued PostgreSQL command must finish first.' }

        $journal=Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($journal.state -notin @('Succeeded','Rejected')) { throw 'Inspect the outstanding PostgreSQL command before restarting the controller.' }
    }
}

$certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2([IO.Path]::GetFullPath($CertificatePath))

try {
    $pin=Get-CertificatePin $certificate
    $clientEku=@($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.2' })

    if ($pin -ine $ExpectedMaintenanceSha256 -or $pin -ieq $network.CertificateSha256 -or $pin -ieq $network.PeerCertificateSha256 -or $certificate.Subject -notlike 'CN=TechMES PostgreSQL Maintenance *' -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or $clientEku.Count -ne 1) { throw 'The public Maintenance certificate, independent pin, validity or client purpose differs.' }
}
finally {
    $certificate.Dispose()
}

$network | Add-Member -NotePropertyName MaintenanceCertificateSha256 -NotePropertyValue $pin -Force

Write-Settings $settingsPath $settings

$ruleName='TechMES PostgreSQL Maintenance '+$network.NodeAddress

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$null=New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $network.Port -LocalAddress $network.NodeAddress -RemoteAddress $MaintenanceAddress -Profile Any

Restart-Service -Name $service.Name -ErrorAction Stop

[pscustomobject]@{
    Node=$network.NodeAddress
    HostService=$service.Name
    MaintenanceAddress=$MaintenanceAddress
    MaintenanceSha256=$pin
    Settings=$settingsPath
} | Format-List