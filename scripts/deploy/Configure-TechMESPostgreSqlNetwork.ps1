#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Connect')][string]$Mode='Prepare',
    [Parameter(Mandatory=$true)][ValidateScript({ $ip=$_ -as [Net.IPAddress]; $null -ne $ip -and $ip.AddressFamily -eq 'InterNetwork' -and $ip.ToString() -ceq $_ -and $ip.GetAddressBytes()[0] -in 1..223 -and ![Net.IPAddress]::IsLoopback($ip) })][string]$NodeAddress,
    [Parameter(Mandatory=$true)][ValidateScript({ $ip=$_ -as [Net.IPAddress]; $null -ne $ip -and $ip.AddressFamily -eq 'InterNetwork' -and $ip.ToString() -ceq $_ -and $ip.GetAddressBytes()[0] -in 1..223 -and ![Net.IPAddress]::IsLoopback($ip) })][string]$PeerAddress,
    [string]$PeerCertificatePath='',
    [ValidatePattern('^$|^[0-9A-Fa-f]{64}$')][string]$ExpectedPeerSha256='',
    [ValidateRange(1024,65535)][int]$Port=5115,
    [ValidatePattern('^$|^[0-9]{1,20}$')][string]$ExpectedSystemIdentifier='',
    [ValidatePattern('^$|^[A-Za-z0-9_.-]+$')][string]$ExpectedServiceName='',
    [switch]$AsObject
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run locally as Administrator.' }
} finally { $identity.Dispose() }

if ($NodeAddress -notin @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)) { throw 'NodeAddress does not belong to this computer.' }

if ($NodeAddress -eq $PeerAddress) { throw 'NodeAddress and PeerAddress must differ.' }
$runtime=Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service'"
$agent=Get-CimInstance Win32_Service -Filter "Name='TechMES.PostgreSql.Agent'"
$service=if ($null -ne $runtime) { $runtime } elseif ($null -ne $agent) { $agent } else { throw 'Install Runtime or PostgreSql.Agent first.' }

if ($service.PathName -match '^\s*"([^"]+)"') { $executable=$matches[1] }
elseif ($service.PathName -match '^\s*(\S+\.exe)(?:\s|$)') { $executable=$matches[1] }
else { throw 'Cannot read the service executable.' }

if ([IO.Path]::GetFileName($executable) -ine ($service.Name+'.exe')) { throw 'Use the standard TechMES apphost executable.' }

$directory=[IO.Path]::GetDirectoryName($executable)
$settingsPath=Join-Path $directory 'appsettings.PostgreSqlControl.json'
# Сеть настраивается поверх конфигурации, созданной штатной локальной подготовкой контроллера.
if (!(Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw ('Controller settings are missing: ' + $settingsPath + '. Use Preparing the controller in Maintenance, then run this network setup again.')
}
$settings=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$control=$settings.PostgreSqlControl

if ($ExpectedServiceName -and $control.ServiceName -ine $ExpectedServiceName) { throw 'The selected controller belongs to another PostgreSQL service.' }

if ($ExpectedSystemIdentifier) {
    $existing=$control.PSObject.Properties['ExpectedSystemIdentifier']
    if ($existing -and $existing.Value -and $existing.Value -cne $ExpectedSystemIdentifier) {
        throw 'The expected cluster differs from the configured controller.'
    }

    # A prepared local controller can identify an existing cluster before command access is enabled.
    $statusPath=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$control.ServiceName.ToUpperInvariant()+'\status.json')
    $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    $now=[DateTimeOffset]::UtcNow
    $checked=[DateTimeOffset]$status.checkedAtUtc
    if (!$status.ownsController -or !$status.permissionsOk -or $status.controllerState -ne 'Monitoring' -or !$status.service.managedByService -or $status.service.computer -ine $env:COMPUTERNAME -or $status.settings.serviceName -ine $control.ServiceName -or $status.service.port -ne $control.Port -or $status.systemIdentifier -cne $ExpectedSystemIdentifier -or $checked -gt $now.AddMinutes(1) -or ($now-$checked).TotalSeconds -gt [Math]::Max(30,3*$status.healthPeriodSeconds)) {
        throw 'The local PostgreSQL controller could not confirm the expected cluster and service.'
    }
    $control | Add-Member -NotePropertyName CommandsEnabled -NotePropertyValue $true -Force
    $control | Add-Member -NotePropertyName ExpectedSystemIdentifier -NotePropertyValue $ExpectedSystemIdentifier -Force
}

if (!$control.CommandsEnabled -or $control.ExpectedSystemIdentifier -notmatch '^[0-9]{1,20}$') { throw 'Configure CommandsEnabled and ExpectedSystemIdentifier first.' }
if ($Port -eq $control.Port) { throw 'Use a separate HTTPS port.' }

# Вычисляет SHA-256 без зависимости от новых API .NET Framework.
function Get-CertificatePin([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)).Replace('-','') }
    finally { $sha.Dispose() }
}

$property=$settings.PSObject.Properties['PostgreSqlNetwork']
$network=if ($null -ne $property -and $null -ne $property.Value) { $property.Value } else { [pscustomobject]@{} }
$priorEnabled=$network.PSObject.Properties['Enabled']
if ($ExpectedSystemIdentifier -and $priorEnabled -and $priorEnabled.Value -and ($network.NodeAddress -ne $NodeAddress -or $network.PeerAddress -ne $PeerAddress -or $network.Port -ne $Port)) {
    throw 'The active network profile belongs to another pair or HTTPS port.'
}
$localPinProperty=$network.PSObject.Properties['CertificateSha256']
$certificate=$null

if ($null -ne $localPinProperty -and $localPinProperty.Value) {
    $certificate=@(Get-ChildItem Cert:\LocalMachine\My | Where-Object { (Get-CertificatePin $_) -ceq $localPinProperty.Value }) | Select-Object -First 1
    if ($null -eq $certificate -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The configured local certificate is missing, expired or has no private key.' }
}
elseif ($Mode -eq 'Prepare') {
    # Ключ остаётся в LocalMachine; копируется только публичный .cer.
    $extensions=@(('2.5.29.17={text}IPAddress='+$NodeAddress),'2.5.29.37={text}1.3.6.1.5.5.7.3.1,1.3.6.1.5.5.7.3.2')
    $certificate=New-SelfSignedCertificate -Type Custom -Subject ('CN=TechMES PostgreSQL '+$NodeAddress) -FriendlyName ('TechMES PostgreSQL '+$NodeAddress) -TextExtension $extensions -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature,KeyEncipherment -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter ((Get-Date).AddYears(2))
}
else { throw 'Run -Mode Prepare on this node first.' }

$localPin=Get-CertificatePin $certificate
$enabledProperty=$network.PSObject.Properties['Enabled']
$enabled=if ($null -ne $enabledProperty) { [bool]$enabledProperty.Value } else { $false }
$peerPinProperty=$network.PSObject.Properties['PeerCertificateSha256']
$peerPin=if ($null -ne $peerPinProperty) { [string]$peerPinProperty.Value } else { '' }

if ($Mode -eq 'Connect') {
    if (!$PeerCertificatePath -or !$ExpectedPeerSha256) { throw 'Specify PeerCertificatePath and ExpectedPeerSha256 from the other node Prepare output.' }

    $peer=New-Object Security.Cryptography.X509Certificates.X509Certificate2([IO.Path]::GetFullPath($PeerCertificatePath))

    try {
        if ($peer.Subject -cne ('CN=TechMES PostgreSQL '+$peerAddress) -or $peer.NotAfter -le (Get-Date) -or $peer.NotBefore -gt (Get-Date)) { throw 'The public certificate does not identify the expected peer or is expired.' }

        $peerPin=Get-CertificatePin $peer

        if (!$peerPin.Equals($ExpectedPeerSha256,[StringComparison]::OrdinalIgnoreCase)) { throw 'The copied certificate does not match the independently supplied peer pin.' }
    }
    finally { $peer.Dispose() }

    if ($peerPin -ceq $localPin) { throw 'The local and peer certificates must be different.' }

    $enabled=$true
}

$values=[ordered]@{ Enabled=$enabled; NodeAddress=$NodeAddress; PeerAddress=$peerAddress; Port=$Port; CertificateSha256=$localPin; PeerCertificateSha256=$peerPin }

foreach ($key in $values.Keys) {
    $network | Add-Member -NotePropertyName $key -NotePropertyValue $values[$key] -Force
}

$settings | Add-Member -NotePropertyName PostgreSqlNetwork -NotePropertyValue $network -Force

# Сохраняет прежние секции и делает резервную копию JSON.
$temporary=$settingsPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
$backup=$settingsPath+'.before-network-'+[guid]::NewGuid().ToString('N')+'.bak'

[IO.File]::WriteAllText($temporary,($settings | ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($true)))
[IO.File]::Replace($temporary,$settingsPath,$backup)

$publicPath=Join-Path $directory ('TechMES-PostgreSql-'+$NodeAddress+'.cer')
$null=Export-Certificate -Cert $certificate -FilePath $publicPath -Type CERT -Force

if ($Mode -eq 'Connect') {
    # Доступ к порту ограничен двумя адресами; перезапускается хост TechMES.
    $ruleName='TechMES PostgreSQL mTLS '+$NodeAddress

    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    $null=New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -LocalAddress $NodeAddress -RemoteAddress @($NodeAddress,$peerAddress) -Profile Any

    Restart-Service -Name $service.Name -ErrorAction Stop
}

$result=[pscustomobject]@{
    Node=$NodeAddress
    Peer=$peerAddress
    HostService=$service.Name
    CertificateSha256=$localPin
    PeerCertificateSha256=$peerPin
    PublicCertificate=$publicPath
    Settings=$settingsPath
    NetworkEnabled=$enabled
}
if ($AsObject) { $result } else { $result | Format-List }
