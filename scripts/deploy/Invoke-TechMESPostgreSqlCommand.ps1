#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Credentials','Status','Fence','Promote','Rejoin')][string]$Mode='Status', [ValidateSet('192.168.1.132','192.168.1.131')][string]$From='192.168.1.131', [ValidateSet('192.168.1.132','192.168.1.131')][string]$To='192.168.1.132', [string]$FenceFile='', [ValidatePattern('^[0-9]{1,20}$')][string]$SystemIdentifier='7703468038317098096', [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18', [ValidatePattern('^$|^[0-9a-f]{32}$')][string]$OperationId='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run locally on the Windows database server.' }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
} finally { $identity.Dispose() }

$root=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$ServiceName.ToUpperInvariant()+'\Commands')

# Запрещает ссылки в каталоге очереди и всех его существующих родителях.
function Assert-NoLinks([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed in the command store.' }
        $current=Split-Path -Path $current -Parent
    }
}

# Создаёт закрытый каталог до записи зашифрованного секрета.
function Initialize-Store {
    Assert-NoLinks $root
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $admins=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($admins)
    foreach ($sid in @('S-1-5-32-544','S-1-5-18')) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
    $directory=New-Object IO.DirectoryInfo($root)
    if ($directory.Exists) { Set-Acl -LiteralPath $root -AclObject $acl } else { $directory.Create($acl) }
}

# Сбрасывает данные на диск перед атомарной публикацией файла.
function Write-Atomic([string]$Path, [byte[]]$Bytes) {
    Assert-NoLinks $Path
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $acl=Get-Acl -LiteralPath $temporary
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        Set-Acl -LiteralPath $temporary -AclObject $acl
        $stream.Write($Bytes,0,$Bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary,$Path,$null) } else { [IO.File]::Move($temporary,$Path) }
}

if ($Mode -eq 'Status') {
    Assert-NoLinks $root
    if (!(Test-Path -LiteralPath $root -PathType Container)) { Write-Host 'No local command queue. Configure the host and its credentials first.'; return }
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^[0-9a-f]{32}$' } | Sort-Object Name) {
        Assert-NoLinks $directory.FullName
        if ($OperationId -and $directory.Name -cne $OperationId) { continue }
        $journal=Join-Path $directory.FullName 'journal.json'
        Assert-NoLinks $journal
        if (Test-Path -LiteralPath $journal) { Get-Content -LiteralPath $journal -Raw }
        else { Write-Host ('Queued: '+$directory.Name) }
    }
    return
}

Initialize-Store

if ($Mode -eq 'Credentials') {
    Add-Type -AssemblyName System.Security
    $sql=Get-Credential -UserName 'postgres' -Message 'PostgreSQL administration account on both nodes'
    $replication=Get-Credential -UserName 'techmes_repl' -Message 'PostgreSQL replication account'

    if ($null -eq $sql -or $null -eq $replication) { throw 'Credential entry cancelled.' }
    if ($sql.UserName -notmatch '^[A-Za-z0-9_.-]+$' -or $replication.UserName -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Unsupported PostgreSQL username.' }

    $plain=$null
    try {
        $value=[ordered]@{ sqlUser=$sql.UserName; sqlPassword=$sql.GetNetworkCredential().Password; replicationUser=$replication.UserName; replicationPassword=$replication.GetNetworkCredential().Password }
        $plain=[Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Compress))

        if ($plain.Length -gt 32000) { throw 'Credential input is too large.' }

        $entropy=[Text.Encoding]::UTF8.GetBytes('TechMES.PostgreSql.Commands.v1')
        $protected=[Security.Cryptography.ProtectedData]::Protect($plain,$entropy,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        $credentialPath=Join-Path $root 'credentials.bin'

        Assert-NoLinks $credentialPath

        if (Test-Path -LiteralPath $credentialPath) {
            $acl=New-Object Security.AccessControl.FileSecurity
            $acl.SetAccessRuleProtection($true,$false)
            $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
            foreach ($sid in @('S-1-5-32-544','S-1-5-18')) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','Allow'))) }
            Set-Acl -LiteralPath $credentialPath -AclObject $acl
        }

        Write-Atomic $credentialPath $protected
    }
    finally {
        if ($null -ne $plain) { [Array]::Clear($plain,0,$plain.Length) }
        $value=$null
        $sql=$null
        $replication=$null
    }

    Write-Host 'Local credentials saved for SYSTEM and Administrators. Repeat on the other database server.' -ForegroundColor Green
    return
}

if ($From -eq $To) { throw 'From and To must be different database nodes.' }

$statusPath=Join-Path (Split-Path -Path $root -Parent) 'status.json'
Assert-NoLinks $statusPath

if (!(Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'The local host has not published its status yet.' }

$status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json

if (!$status.ownsController -or !$status.settings.commandsEnabled -or $status.settings.expectedSystemIdentifier -cne $SystemIdentifier -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($status.checkedAtUtc)).TotalSeconds -gt [Math]::Max(60,2*$status.healthPeriodSeconds)) { throw 'The local controller is not ready. Check CommandsEnabled, ExpectedSystemIdentifier, host status and existing operations.' }

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
$expected=if ($Mode -eq 'Promote') { $To } else { $From }

if ($expected -notin $addresses -or @($From,$To | Where-Object { $_ -in $addresses }).Count -ne 1) { throw 'Run this command locally on the correct database server.' }
if (!(Test-Path -LiteralPath (Join-Path $root 'credentials.bin') -PathType Leaf)) { throw 'Run -Mode Credentials on this server first.' }

$proof=$null

if ($Mode -eq 'Fence') {
    if ($FenceFile) { throw 'Fence generates its own exchange file. Do not specify FenceFile.' }
}
else {
    if (!$FenceFile) { throw 'Specify the fence.json generated by the completed Fence operation.' }

    Assert-NoLinks $FenceFile

    if ((Get-Item -LiteralPath $FenceFile).Length -gt 32768) { throw 'The fence file is too large.' }

    $proof=Get-Content -LiteralPath $FenceFile -Raw | ConvertFrom-Json

    if ($proof.State -ne 'Fenced' -or $proof.From -ne $From -or $proof.To -ne $To -or $proof.Sid -cne $SystemIdentifier) { throw 'The fence proof does not match this switch.' }
}

if (!$OperationId) { $OperationId=[guid]::NewGuid().ToString('N') }

$directory=Join-Path $root $OperationId

if (Test-Path -LiteralPath $directory) { throw 'OperationId already exists. Use -Mode Status to inspect it; do not resubmit.' }

$operationAcl=Get-Acl -LiteralPath $root
(New-Object IO.DirectoryInfo($directory)).Create($operationAcl)

$request=[ordered]@{ id=$OperationId; mode=$Mode; from=$From; to=$To; systemIdentifier=$SystemIdentifier; createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); fence=$proof }
$bytes=[Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 12))

if ($bytes.Length -gt 65536) { throw 'The request is too large.' }

Write-Atomic (Join-Path $directory 'request.json') $bytes

Write-Host ('Queued operation: '+$OperationId) -ForegroundColor Cyan
Write-Host ('Directory: '+$directory)
Write-Host 'Submission does not confirm completion. Use -Mode Status; retain the directory and logs.'