#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('TechMES.Runtime.Service','TechMES.PostgreSql.Agent')][string]$HostService, [Parameter(Mandatory=$true)][ValidateSet('192.168.1.131','192.168.1.132')][string]$NodeAddress, [Parameter(Mandatory=$true)][ValidateSet('192.168.1.131','192.168.1.132')][string]$PeerAddress, [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$NodeSha256, [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$PeerSha256, [Parameter(Mandatory=$true)][ValidatePattern('^[0-9]{1,20}$')][string]$SystemIdentifier, [Parameter(Mandatory=$true)][string]$MaintenanceAddress, [Parameter(Mandatory=$true)][string]$MaintenanceCertificatePath, [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$MaintenanceSha256, [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18', [ValidateRange(1,65535)][int]$PgPort=5432, [ValidateRange(1024,65535)][int]$HttpsPort=5115, [ValidateRange(15,300)][int]$ReadyTimeoutSeconds=60)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'

# Normalizes local paths and rejects links before reading or replacing files.
function Assert-NoLinks([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)

    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point: $current"
        }

        $current=Split-Path -Path $current -Parent
    }
}

# Reads registered executable paths without invoking a shell.
function Get-Executable([string]$Command) {
    $match=[regex]::Match($Command,'^\s*(?:"([^"]+\.exe)"|(.+?\.exe))(?:\s|$)','IgnoreCase')

    if (!$match.Success) {
        throw 'Cannot read a registered service executable.'
    }

    if ($match.Groups[1].Success) {
        return $match.Groups[1].Value
    }

    return $match.Groups[2].Value
}

# Bounds read-only child processes; never invokes pg_ctl start/stop/promote.
function Invoke-ReadOnly([string]$Executable, [string[]]$Arguments) {
    $quoted=@(
        $Arguments | ForEach-Object {
            if ($_ -match '["\r\n]' -or $_.EndsWith('\')) {
                throw 'Unexpected diagnostic argument.'
            }

            '"'+$_+'"'
        }
    )

    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$Executable
    $info.Arguments=$quoted -join ' '
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    $info.EnvironmentVariables['LC_ALL']='C'

    $process=New-Object Diagnostics.Process
    $process.StartInfo=$info

    try {
        $null=$process.Start()

        $output=$process.StandardOutput.ReadToEndAsync()
        $errors=$process.StandardError.ReadToEndAsync()

        if (!$process.WaitForExit(20000)) {
            $process.Kill()
            throw 'A read-only diagnostic timed out.'
        }

        $text=$output.GetAwaiter().GetResult()
        $errorText=$errors.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            throw ('Read-only diagnostic failed: '+$errorText)
        }

        return $text
    }
    finally {
        $process.Dispose()
    }
}

# Returns the SHA256 pin without requiring new .NET certificate APIs.
function Get-Pin([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha=[Security.Cryptography.SHA256]::Create()

    try {
        return [BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)).Replace('-','')
    }
    finally {
        $sha.Dispose()
    }
}

# Requires a valid certificate and explicitly configured purposes.
function Assert-Certificate([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$Pin, [bool]$Node) {
    if ($null -eq $Certificate -or (Get-Pin $Certificate) -ine $Pin -or $Certificate.NotBefore -gt (Get-Date) -or $Certificate.NotAfter -le (Get-Date)) {
        throw 'A pinned certificate is missing, different or expired.'
    }

    if ($Node -and !$Certificate.HasPrivateKey) {
        throw 'The original node certificate has no accessible private key.'
    }

    $oids=@(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
            ForEach-Object {
                ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages
            } |
            ForEach-Object { $_.Value }
    )

    if ('1.3.6.1.5.5.7.3.2' -notin $oids -or ($Node -and '1.3.6.1.5.5.7.3.1' -notin $oids)) {
        throw 'Unexpected certificate purposes.'
    }
}

# Refuses to restart a host reserved by an unfinished switch or command.
function Assert-NoPending([string]$Root) {
    Assert-NoLinks $Root

    foreach ($name in @('Network\active-switch.json','Network\console-active.json')) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) {
            throw 'An unfinished switch exists. Preserve its journal and inspect it first.'
        }
    }

    if (!(Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    foreach ($folder in Get-ChildItem -LiteralPath $Root -Directory | Where-Object { $_.Name -match '^[0-9a-f]{32}$' }) {
        Assert-NoLinks $folder.FullName

        if (!(Test-Path -LiteralPath (Join-Path $folder.FullName 'request.json'))) {
            continue
        }

        $journalPath=Join-Path $folder.FullName 'journal.json'

        if (!(Test-Path -LiteralPath $journalPath)) {
            throw 'A queued PostgreSQL command exists.'
        }

        Assert-NoLinks $journalPath

        $journal=Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($journal.state -notin @('Succeeded','Rejected')) {
            throw ('An unfinished command exists: '+$folder.Name)
        }
    }
}

# Confirms that previously saved credentials still meet the reader's ACL rules.
function Assert-Credentials([string]$Path) {
    Assert-NoLinks $Path

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Local command credentials are missing. Run Invoke-TechMESPostgreSqlCommand.ps1 -Mode Credentials first.'
    }

    if ((Get-Item -LiteralPath $Path).Length -lt 1 -or (Get-Item -LiteralPath $Path).Length -gt 65536) {
        throw 'Invalid local credentials file size.'
    }

    $acl=Get-Acl -LiteralPath $Path

    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) {
        throw 'Unexpected credentials owner.'
    }

    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) {
            throw 'Credentials ACL is not private.'
        }
    }
}

# Reuses existing sections and preserves unrelated configuration.
function Merge-Section($Settings, [string]$Name, $Values) {
    $property=$Settings.PSObject.Properties[$Name]

    $section=if ($null -ne $property -and $null -ne $property.Value) {
        $property.Value
    }
    else {
        [pscustomobject]@{}
    }

    foreach ($key in $Values.Keys) {
        $section | Add-Member -NotePropertyName $key -NotePropertyValue $Values[$key] -Force
    }

    $Settings | Add-Member -NotePropertyName $Name -NotePropertyValue $section -Force
}

# A per-request native callback verifies the pin and host name.
function Initialize-HttpsProbe {
    if ('TechMES.Tools.PgHostHttpsProbe' -as [type]) {
        return
    }

    $code=@'
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TechMES.Tools
{
    public static class PgHostHttpsProbe
    {
        public static string Get(string uri, X509Certificate2 client, string pin)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Timeout = 3000;
            request.ReadWriteTimeout = 3000;
            request.ClientCertificates.Add(client);

            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                if (certificate == null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                    return false;

                using (var hash = SHA256.Create())
                {
                    return BitConverter.ToString(hash.ComputeHash(certificate.GetRawCertData())).Replace("-", "").Equals(pin, StringComparison.OrdinalIgnoreCase);
                }
            };

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Unexpected HTTP status.");

                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var text = reader.ReadToEnd();

                    if (text.Length > 1048576)
                        throw new IOException("Unexpected response size.");

                    return text;
                }
            }
        }
    }
}
'@

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        Add-Type -TypeDefinition $code -IgnoreWarnings
    }
    else {
        Add-Type -TypeDefinition $code
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run locally on Windows as Administrator.'
}

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run as Administrator.'
    }
}
finally {
    $identity.Dispose()
}

if ($NodeAddress -eq $PeerAddress -or $NodeSha256 -ieq $PeerSha256 -or $MaintenanceSha256 -ieq $NodeSha256 -or $MaintenanceSha256 -ieq $PeerSha256 -or $PgPort -eq $HttpsPort) {
    throw 'Node addresses, certificate pins and service ports must be distinct.'
}

$ip=$null

if (![Net.IPAddress]::TryParse($MaintenanceAddress,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip)) {
    throw 'Invalid Maintenance IPv4 address.'
}

if ($NodeAddress -notin @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)) {
    throw 'NodeAddress does not belong to this computer.'
}

$hostInfo=Get-CimInstance Win32_Service -Filter ("Name='"+$HostService+"'")

if ($null -eq $hostInfo) {
    throw 'The selected TechMES host service is not installed.'
}

if ($HostService -eq 'TechMES.PostgreSql.Agent' -and $null -ne (Get-CimInstance Win32_Service -Filter "Name='TechMES.Runtime.Service'")) {
    throw 'Runtime is installed here. Its controller has priority; inspect the intended host before repairing Agent.'
}

$executable=Get-Executable $hostInfo.PathName

if ([IO.Path]::GetFileName($executable) -ine ($HostService+'.exe')) {
    throw 'Unexpected TechMES apphost.'
}

$hostDirectory=[IO.Path]::GetDirectoryName($executable)
$settingsPath=Join-Path $hostDirectory 'appsettings.PostgreSqlControl.json'

Assert-NoLinks $settingsPath

$settings=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pgService=Get-CimInstance Win32_Service -Filter ("Name='"+$ServiceName+"'")

if ($null -eq $pgService) {
    throw 'The PostgreSQL Windows service is not installed.'
}

$pgExecutable=Get-Executable $pgService.PathName

if ([IO.Path]::GetFileName($pgExecutable) -ine 'pg_ctl.exe') {
    throw 'Unexpected PostgreSQL service executable.'
}

$pgBin=[IO.Path]::GetDirectoryName($pgExecutable)
$dataMatch=[regex]::Match($pgService.PathName,'(?:^|\s)-D\s+(?:"([^"]+)"|(\S+))','IgnoreCase')

if (!$dataMatch.Success) {
    throw 'Cannot read the registered PostgreSQL data directory.'
}

$dataDirectory=if ($dataMatch.Groups[1].Success) {
    $dataMatch.Groups[1].Value
}
else {
    $dataMatch.Groups[2].Value
}

$dataDirectory=[IO.Path]::GetFullPath($dataDirectory).TrimEnd('\','/')

Assert-NoLinks $dataDirectory

$storeRoot=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$ServiceName.ToUpperInvariant()+'\Commands')

Assert-NoPending $storeRoot
Assert-Credentials (Join-Path $storeRoot 'credentials.bin')

$statusTool=Join-Path $hostDirectory 'Tools\Set-PostgreSQL18ServiceControl.ps1'

if (!(Test-Path -LiteralPath $statusTool -PathType Leaf)) {
    throw 'The published service status tool is missing.'
}

$powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$pgStatus=Invoke-ReadOnly $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$statusTool,'-Mode','Status','-AsJson','-ServiceName',$ServiceName,'-PgBin',$pgBin,'-DataDirectory',$dataDirectory,'-Port',[string]$PgPort) | ConvertFrom-Json

if (!$pgStatus.ManagedByService) {
    throw 'PostgreSQL must be running under its existing Windows service.'
}

$controlText=Invoke-ReadOnly (Join-Path $pgBin 'pg_controldata.exe') @('-D',$dataDirectory)
$sidMatch=[regex]::Match($controlText,'(?m)^Database system identifier:\s*([0-9]+)\s*$')

if (!$sidMatch.Success -or $sidMatch.Groups[1].Value -cne $SystemIdentifier) {
    throw 'The actual PostgreSQL cluster SID differs. No settings were changed.'
}

$nodeCertificate=@(
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { (Get-Pin $_) -ieq $NodeSha256 }
) | Select-Object -First 1

Assert-Certificate $nodeCertificate $NodeSha256 $true

Assert-NoLinks $MaintenanceCertificatePath

$maintenanceCertificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2([IO.Path]::GetFullPath($MaintenanceCertificatePath))

try {
    Assert-Certificate $maintenanceCertificate $MaintenanceSha256 $false
}
finally {
    $maintenanceCertificate.Dispose()
}

# Restore both prerequisites and network configuration in one backed-up write.
Merge-Section $settings 'PostgreSqlControl' ([ordered]@{Enabled=$true;AutoDetect=$true;ServiceName=$ServiceName;BinDirectory=$pgBin;DataDirectory=$dataDirectory;Port=$PgPort;HealthPeriodSeconds=10;CommandsEnabled=$true;ExpectedSystemIdentifier=$SystemIdentifier})

Merge-Section $settings 'PostgreSqlNetwork' ([ordered]@{Enabled=$true;NodeAddress=$NodeAddress;PeerAddress=$PeerAddress;Port=$HttpsPort;CertificateSha256=$NodeSha256.ToUpperInvariant();PeerCertificateSha256=$PeerSha256.ToUpperInvariant();MaintenanceCertificateSha256=$MaintenanceSha256.ToUpperInvariant()})

Initialize-HttpsProbe

Assert-NoPending $storeRoot

$backup=$settingsPath+'.before-host-repair-'+[guid]::NewGuid().ToString('N')+'.bak'
$temporary=$settingsPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'

[IO.File]::WriteAllText($temporary,($settings | ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($true)))
[IO.File]::Replace($temporary,$settingsPath,$backup)

Write-Host ('BACKUP='+$backup)

foreach ($rule in @(
    @{Name='TechMES PostgreSQL mTLS '+$NodeAddress;Remote=@($NodeAddress,$PeerAddress)},
    @{Name='TechMES PostgreSQL Maintenance '+$NodeAddress;Remote=@($MaintenanceAddress)}
)) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    $null=New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort -LocalAddress $NodeAddress -RemoteAddress $rule.Remote -Profile Any
}

$startedAt=[DateTimeOffset]::UtcNow

Restart-Service -Name $HostService -ErrorAction Stop

$deadline=[DateTimeOffset]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
$lastError='No response.'
$previousTls=[Net.ServicePointManager]::SecurityProtocol

try {
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12

    do {
        try {
            $response=[TechMES.Tools.PgHostHttpsProbe]::Get(('https://'+$NodeAddress+':'+$HttpsPort+'/v1/status'),$nodeCertificate,$NodeSha256) | ConvertFrom-Json

            $local=$response.local

            $expectedHost=if ($HostService -eq 'TechMES.PostgreSql.Agent') {
                'Standalone'
            }
            else {
                'Runtime'
            }

            $fresh=$null -ne $local.checkedAtUtc -and [DateTimeOffset]$local.checkedAtUtc -ge $startedAt -and ([DateTimeOffset]::UtcNow-[DateTimeOffset]$local.checkedAtUtc).TotalSeconds -le 60

            if ($response.nodeAddress -eq $NodeAddress -and $fresh -and $local.hostKind -eq $expectedHost -and $local.ownsController -and $local.permissionsOk -and $local.controllerState -eq 'Monitoring' -and $local.service.managedByService -and $local.systemIdentifier -ceq $SystemIdentifier -and $local.settings.commandsEnabled -and $local.settings.expectedSystemIdentifier -ceq $SystemIdentifier -and $local.postgreSqlRole -in @('Primary','Standby')) {
                [pscustomobject]@{
                    Computer=$env:COMPUTERNAME
                    HostService=$HostService
                    Node=$NodeAddress
                    HttpsReady=$true
                    ControllerState=$local.controllerState
                    PostgreSqlRole=$local.postgreSqlRole
                    CommandsEnabled=$local.settings.commandsEnabled
                    SystemIdentifier=$local.systemIdentifier
                    Settings=$settingsPath
                    Backup=$backup
                } | Format-List

                return
            }

            $lastError='HTTPS answered, but controller readiness was not confirmed: '+$local.controllerState+'; '+$local.message
        }
        catch {
            $lastError=$_.Exception.GetBaseException().Message
        }

        Write-Host ('Waiting for controller: '+$lastError)

        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
}
finally {
    [Net.ServicePointManager]::SecurityProtocol=$previousTls
}

$snapshotPath=Join-Path (Split-Path -Path $storeRoot -Parent) 'status.json'

if (Test-Path -LiteralPath $snapshotPath -PathType Leaf) {
    $snapshot=Get-Content -LiteralPath $snapshotPath -Raw -Encoding UTF8 | ConvertFrom-Json

    $snapshot |
        Select-Object checkedAtUtc,hostKind,controllerState,ownsController,permissionsOk,windowsError,postgreSqlRole,systemIdentifier,message |
        Format-List
}

throw ('Settings were backed up and written, but HTTPS readiness failed. Backup: '+$backup+'; Last error: '+$lastError)