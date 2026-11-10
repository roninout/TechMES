#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param([ValidatePattern('^[0-9a-f]{32}$')][string]$OperationId='ce5c2ed5c610fb69d9ec7180c514d71b')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# Это восстановление записи Fence после конкретной ошибки File.Replace, без повторной остановки БД.
$serviceName='postgresql-x64-18'
$hostService='TechMES.PostgreSql.Agent'
$pgBin='D:\PostgreSQL\18\bin'
$dataDirectory='D:\PostgreSQL\18\data'
$sid='7703468038317098096'
$from='192.168.1.131'
$to='192.168.1.132'
$nodeRoot=Join-Path $env:ProgramData 'TechMES\PostgreSqlNodes\POSTGRESQL-X64-18'
$commandsRoot=Join-Path $nodeRoot 'Commands'
$directory=Join-Path $commandsRoot $OperationId
$networkRoot=Join-Path $commandsRoot 'Network'
$activePath=Join-Path $env:ProgramData 'TechMES\PostgreSqlSetup\Switches\postgresql-x64-18-active-fence.json'
$backupRoot=Join-Path $directory 'fence-repair'
$serviceTool=Join-Path $PSScriptRoot 'Set-PostgreSQL18ServiceControl.ps1'
$powerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# Исключает ссылки, подмену владельца и доступ посторонних к очереди.
function Assert-PrivatePath([string]$Path, [switch]$AllowRead) {
    $current=[IO.Path]::GetFullPath($Path)

    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse point: '+$current) }

        $current=Split-Path -Path $current -Parent
    }

    $acl=Get-Acl -LiteralPath $Path

    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw ('Unexpected owner: '+$Path) }

    $writeRights=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership

    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and (!$AllowRead -or ($rule.FileSystemRights -band $writeRights))) { throw ('Unexpected access rule: '+$Path) }
    }
}

# Читает только небольшой локальный JSON; пароли для восстановления не нужны.
function Read-Json([string]$Path) {
    $private=[IO.Path]::GetFullPath($Path).StartsWith($commandsRoot+'\',[StringComparison]::OrdinalIgnoreCase)

    Assert-PrivatePath $Path -AllowRead:(!$private)

    if ((Get-Item -LiteralPath $Path).Length -gt 65536) { throw 'JSON file is too large.' }

    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

# Сохраняет JSON атомарно, с настоящим .NET null и владельцем Administrators.
function Write-Json([string]$Path, $Value) {
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)

    try {
        $acl=Get-Acl -LiteralPath $temporary
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        Set-Acl -LiteralPath $temporary -AclObject $acl

        $bytes=(New-Object Text.UTF8Encoding($true)).GetBytes(($Value | ConvertTo-Json -Depth 12))
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    if ([IO.File]::Exists($Path)) {
        [IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value)
    }
    else {
        [IO.File]::Move($temporary,$Path)
    }
}

# Повторно подтверждает остановку, запрет автозапуска и checkpoint исходного Primary.
function Assert-OfflineFence($Proof) {
    $raw=& $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $serviceTool -Mode Status -AsJson -ServiceName $serviceName -PgBin $pgBin -DataDirectory $dataDirectory -Port 5432

    if ($LASTEXITCODE -ne 0) { throw 'The service ownership check failed.' }

    $state=($raw -join "`n") | ConvertFrom-Json
    $service=Get-CimInstance Win32_Service -Filter "Name='$serviceName'"

    if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled' -or $service.ProcessId -ne 0 -or $state.ServiceState -ne 'Stopped' -or $state.PostmasterProcessId -ne 0 -or $state.ListenerProcessId -ne 0) { throw 'PostgreSQL must remain Stopped/Disabled with no postmaster or listener.' }

    if (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'standby.signal') -PathType Leaf) -or (Test-Path -LiteralPath (Join-Path $dataDirectory 'recovery.signal'))) { throw 'The persistent recovery signals do not match Fence.' }

    $previousLocale=$env:LC_ALL

    try {
        $env:LC_ALL='C'
        $control=& (Join-Path $pgBin 'pg_controldata.exe') -D $dataDirectory

        if ($LASTEXITCODE -ne 0) { throw 'pg_controldata failed.' }
    }
    finally {
        $env:LC_ALL=$previousLocale
    }

    $values=@{}

    foreach ($line in $control) {
        if ($line -match '^([^:]+):\s*(.*?)\s*$') { $values[$matches[1].Trim()]=$matches[2] }
    }

    if ($values['Database cluster state'] -ne 'shut down' -or $values['Database system identifier'] -cne $sid -or $values["Latest checkpoint's TimeLineID"] -ne [string]$Proof.Timeline -or $values['Latest checkpoint location'] -cne $Proof.Lsn) { throw 'Offline SID, timeline, checkpoint or clean shutdown does not match the saved Fence proof.' }
}

# Не позволяет превратить другой отказ или чужой proof в успешный Fence.
function Assert-Evidence($Request, $Journal, $Result, $Active, $Proof, $Plan) {
    if ($Request.id -cne $OperationId -or $Request.mode -cne 'Fence' -or $Request.from -cne $from -or $Request.to -cne $to -or $Request.systemIdentifier -cne $sid -or $null -ne $Request.fence) { throw 'This is not the expected Fence request.' }

    if ($Journal.id -cne $OperationId -or $Journal.state -cne 'NeedsReview' -or $Result.OperationId -cne $OperationId -or $Result.Mode -cne 'Fence' -or $Result.From -cne $from -or $Result.To -cne $to -or $Result.ExpectedSystemIdentifier -cne $sid -or $Result.Success -ne $false -or $Result.Error -notmatch 'Replace') { throw 'The original failure is not the known File.Replace Fence failure.' }

    if ($Plan.from -cne $from -or $Plan.to -cne $to -or $Plan.systemIdentifier -cne $sid -or $Plan.id -notmatch '^[0-9a-f]{32}$') { throw 'The reserved network plan differs.' }

    $hash=[Security.Cryptography.SHA256]::Create()

    try {
        $bytes=$hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Plan.id+'|'+$from+'|'+$to+'|'+$sid+'|Fence')))
        $expected=([BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()).Substring(0,32)
    }
    finally {
        $hash.Dispose()
    }

    if ($expected -cne $OperationId) { throw 'The operation does not belong to the reserved plan.' }

    foreach ($item in @($Active,$Proof)) {
        if ($item.Format -ne 1 -or $item.Id -notmatch '^[0-9a-f]{32}$' -or $item.From -cne $from -or $item.To -cne $to -or $item.Sid -cne $sid -or $item.ServiceName -cne $serviceName -or $item.PgBin -ine $pgBin -or $item.DataDirectory -ine $dataDirectory -or $item.Port -ne 5432 -or $item.StartMode -notin @('Auto','Manual') -or $item.Timeline -lt 1) { throw 'The saved fence profile differs.' }
    }

    if ($Active.State -cne 'Fencing' -or $Active.Lsn -ne '' -or $Proof.State -cne 'Fenced' -or $Proof.Lsn -notmatch '^[0-9A-F]+/[0-9A-F]+$' -or $Proof.Id -cne $Active.Id -or $Proof.Timeline -ne $Active.Timeline -or $Proof.StartMode -cne $Active.StartMode -or (($Proof.Configs | ConvertTo-Json -Compress) -cne ($Active.Configs | ConvertTo-Json -Compress))) { throw 'The intended Fenced update does not match the original Fencing record.' }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run on the Windows database server .131.' }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
}
finally {
    $identity.Dispose()
}

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)

if ($from -notin $addresses -or $to -in $addresses) { throw 'This recovery is only for the source node 192.168.1.131.' }
if (!(Test-Path -LiteralPath $serviceTool -PathType Leaf)) { throw 'Place this script beside Set-PostgreSQL18ServiceControl.ps1.' }

Assert-PrivatePath $commandsRoot
Assert-PrivatePath $directory
Assert-PrivatePath $networkRoot

if (!$PSCmdlet.ShouldProcess($OperationId,'Verify the stopped PostgreSQL and repair the saved Fence confirmation')) { return }

$consoleLock=$null
$controllerLock=$null
$mutex=New-Object Threading.Mutex($false,('Global\TechMES.PostgreSqlSetup.'+$serviceName))
$locked=$false
$restartHost=$false

try {
    # Исключает параллельный координатор, ручной switch и локального исполнителя.
    $consoleLock=[IO.File]::Open((Join-Path $networkRoot 'console.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)

    try {
        $locked=$mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $locked=$true
    }

    if (!$locked) { throw 'Another local PostgreSQL switch script is running.' }

    $journalPath=Join-Path $directory 'journal.json'
    $resultPath=Join-Path $directory 'script-result.json'
    $requestPath=Join-Path $directory 'request.json'
    $fencePath=Join-Path $directory 'fence.json'
    $journal=Read-Json $journalPath

    if ($journal.id -cne $OperationId) { throw 'The operation journal identity differs.' }

    if ($journal.state -eq 'Succeeded') {
        Write-Host 'Fence is already confirmed. Resume the same switch plan.' -ForegroundColor Green
        return
    }

    if ($journal.state -ne 'NeedsReview') { throw 'The operation must be NeedsReview, not Running or Queued.' }

    if (Get-Service -Name 'TechMES.Runtime.Service' -ErrorAction SilentlyContinue) { throw 'This recovery expects the standalone Agent on .131, without an installed Runtime service.' }

    $hostState=Get-Service -Name $hostService

    if ($hostState.Status -notin @('Running','Stopped')) { throw 'The Agent must be Running or Stopped.' }

    if ($hostState.Status -eq 'Running') {
        $restartHost=$true

        Stop-Service -Name $hostService -ErrorAction Stop -Confirm:$false

        (Get-Service -Name $hostService).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(60))
    }

    $controllerLock=[IO.File]::Open((Join-Path $nodeRoot 'controller.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)

    # Исходные файлы сохраняются один раз; повтор после прерывания использует ту же доказательную базу.
    $sources=[ordered]@{
        'request.json'=$requestPath
        'journal.json'=$journalPath
        'script-result.json'=$resultPath
        'active-fence.json'=$activePath
        'intended-fence.json'=($activePath+'.tmp')
    }

    $saved=@{}

    foreach ($name in $sources.Keys) {
        $backupPath=Join-Path $backupRoot $name
        $readPath=if (Test-Path -LiteralPath $backupPath -PathType Leaf) { $backupPath } else { $sources[$name] }
        $saved[$name]=Read-Json $readPath
    }

    $request=Read-Json $requestPath
    $journal=Read-Json $journalPath
    $active=Read-Json $activePath
    $proof=$saved['intended-fence.json']
    $plan=Read-Json (Join-Path $networkRoot 'active-switch.json')
    $consolePlan=Read-Json (Join-Path $networkRoot 'console-active.json')

    if (($consolePlan | ConvertTo-Json -Compress) -cne ($plan | ConvertTo-Json -Compress)) { throw 'The console and node reservations differ.' }

    Assert-Evidence $request $saved['journal.json'] $saved['script-result.json'] $saved['active-fence.json'] $proof $plan

    if ($journal.state -ne 'NeedsReview' -or $journal.fingerprint -cne $saved['journal.json'].fingerprint -or $active.Id -cne $proof.Id -or $active.State -notin @('Fencing','Fenced')) { throw 'The current operation or physical fence was changed.' }

    $fingerprint=(Get-FileHash -LiteralPath $requestPath -Algorithm SHA256).Hash

    if ($fingerprint -cne $journal.fingerprint) { throw 'The immutable request fingerprint differs.' }

    Assert-OfflineFence $proof

    if (!(Test-Path -LiteralPath $backupRoot -PathType Container)) {
        $null=New-Item -ItemType Directory -Path $backupRoot

        Set-Acl -LiteralPath $backupRoot -AclObject (Get-Acl -LiteralPath $directory)
    }

    foreach ($name in $sources.Keys) {
        $backupPath=Join-Path $backupRoot $name

        if (!(Test-Path -LiteralPath $backupPath)) {
            Copy-Item -LiteralPath $sources[$name] -Destination $backupPath

            $acl=Get-Acl -LiteralPath $backupPath
            $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))

            Set-Acl -LiteralPath $backupPath -AclObject $acl
        }
    }

    $auditPath=Join-Path $backupRoot 'verification.json'
    $audit=[ordered]@{
        OperationId=$OperationId
        PlanId=$plan.id
        ProofId=$proof.Id
        Sid=$sid
        Timeline=$proof.Timeline
        Lsn=$proof.Lsn
        State='VerifiedOffline'
        CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        OriginalFiles=$backupRoot
    }

    # Две записи проверяют исправленную замену до изменения результата операции.
    Write-Json $auditPath $audit
    Write-Json $auditPath $audit

    $proof.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')

    Write-Json $activePath $proof
    Write-Json $fencePath $proof

    $result=[ordered]@{
        OperationId=$OperationId
        ExpectedSystemIdentifier=$sid
        Mode='Fence'
        From=$from
        To=$to
        Computer=$env:COMPUTERNAME
        CheckedAtUtc=$proof.CheckedAtUtc
        Success=$true
        Data=[ordered]@{
            State='Fenced'
            ExchangeFile=$fencePath
            FinalCheckpoint=$proof.Lsn
            Recovery='FileReplaceNullString'
            OriginalFiles=$backupRoot
        }
        Error=$null
        Log=$saved['script-result.json'].Log
    }

    Write-Json $resultPath $result

    # Успех записывается последним, после повторной независимой проверки остановленной БД.
    Assert-OfflineFence $proof

    $check=Read-Json $fencePath
    $checkResult=Read-Json $resultPath

    if ($check.Id -cne $proof.Id -or $check.Lsn -cne $proof.Lsn -or !$checkResult.Success -or $checkResult.OperationId -cne $OperationId -or $checkResult.Data.State -cne 'Fenced') { throw 'The repaired files did not pass read-back verification.' }

    $audit.State='ConfirmationPrepared'

    Write-Json $auditPath $audit

    $journal.state='Succeeded'
    $journal.updatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $journal.message='Fence independently verified after the File.Replace failure. Original files and verification are retained in fence-repair.'

    Write-Json $journalPath $journal

    Write-Host ('RECOVERED Fence: '+$OperationId+'; checkpoint='+$proof.Lsn) -ForegroundColor Green
    Write-Host ('Original files: '+$backupRoot)
    Write-Host 'PostgreSQL remains Stopped/Disabled. Resume the same --postgres-switch command; do not start PostgreSQL manually.'
}
finally {
    if ($null -ne $controllerLock) { $controllerLock.Dispose() }
    if ($locked) { $mutex.ReleaseMutex() }

    $mutex.Dispose()

    if ($null -ne $consoleLock) { $consoleLock.Dispose() }
    if ($restartHost) { Start-Service -Name $hostService -ErrorAction Stop -Confirm:$false }
}