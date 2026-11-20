#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param(
    [ValidateSet('Status','Fence','Promote','Rejoin','Rebuild','FailbackReadiness','CheckInitialStandby','InitializeStandby')][string]$Mode='Status',
    [Parameter(Mandatory=$true)][ValidateScript({ $ip=$_ -as [Net.IPAddress]; $null -ne $ip -and $ip.AddressFamily -eq 'InterNetwork' -and $ip.ToString() -ceq $_ -and $ip.GetAddressBytes()[0] -in 1..223 -and ![Net.IPAddress]::IsLoopback($ip) })][string]$From,
    [Parameter(Mandatory=$true)][ValidateScript({ $ip=$_ -as [Net.IPAddress]; $null -ne $ip -and $ip.AddressFamily -eq 'InterNetwork' -and $ip.ToString() -ceq $_ -and $ip.GetAddressBytes()[0] -in 1..223 -and ![Net.IPAddress]::IsLoopback($ip) })][string]$To,
    [string]$FenceFile='',
    [Parameter(Mandatory=$true)][string]$PgBin,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18',
    [ValidateRange(1,65535)][int]$Port=5432,
    [switch]$CredentialInput,
    [ValidatePattern('^$|^[0-9a-f]{32}$')][string]$OperationId='',
    [ValidatePattern('^$|^[0-9a-f]{32}$')][string]$EmergencyFenceId='',
    [ValidatePattern('^$|^[0-9]{1,20}$')][string]$ExpectedSystemIdentifier='',
    [string]$ResultFile='',
    [int]$ExpectedTimeline=0,
    [ValidateRange(0,2147483647)][int]$MaxReplayLagBytes=0,
    [ValidatePattern('^$|^[0-9]{1,20}$')][string]$ExpectedTargetSystemIdentifier=''
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$localSystemSid=[Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid,$null)

$administratorsSid=[Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,$null)
$ProgressPreference='SilentlyContinue'

# Нормализует локальный путь для сравнения и проверки границ каталога.
function Normalize-Path([string]$Value) {
    return [IO.Path]::GetFullPath($Value).TrimEnd('\','/')
}

# Запрещает корневые каталоги, сетевые пути и ссылки в существующей части пути.
function Assert-LocalPath([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:\\' -or $Value -eq [IO.Path]::GetPathRoot($Value).TrimEnd('\')) { throw 'Use a local non-root Windows directory.' }

    $current=$Value

    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Reparse point is not supported: '+$current) }
        }

        $current=Split-Path -Path $current -Parent
    }
}

# Разрешает путь файла конфигурации только внутри локального PGDATA.
function Get-ConfigRelativePath([string]$Value) {
    if (![IO.Path]::IsPathRooted($Value)) { $Value=Join-Path $DataDirectory $Value }

    $full=Normalize-Path $Value
    $prefix=$DataDirectory+'\'

    if (!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw ('External PostgreSQL configuration is not supported by this test: '+$full) }

    Assert-LocalPath $full

    return $full.Substring($prefix.Length)
}

# Экранирует аргумент по правилам Windows argv, включая завершающие обратные слеши.
function Quote-NativeArgument([string]$Value) {
    $builder=New-Object Text.StringBuilder
    $null=$builder.Append('"')
    $slashes=0

    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }

        if ($character -eq '"') {
            $null=$builder.Append(('\' * ($slashes*2+1))).Append('"')
        }
        else {
            $null=$builder.Append(('\' * $slashes)).Append($character)
        }

        $slashes=0
    }

    $null=$builder.Append(('\' * ($slashes*2))).Append('"')

    return $builder.ToString()
}

# Запускает инструмент; изменяющие операции ожидает без принудительного завершения.
function Invoke-Tool([string]$File, [string[]]$Arguments, [string]$InputText='', [PSCredential]$Credential=$null, [string]$Address='', [switch]$Change, [int]$SuccessCodeMaximum=0) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$File
    $start.Arguments=($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)

    foreach ($name in @('PGPASSWORD','PGHOSTADDR','PGSERVICE','PGSERVICEFILE','PGPASSFILE','PGOPTIONS','PGTARGETSESSIONATTRS')) { $start.EnvironmentVariables.Remove($name) }

    $start.EnvironmentVariables['PGHOST']=$Address
    $start.EnvironmentVariables['PGPORT']=[string]$Port
    $start.EnvironmentVariables['PGDATABASE']='postgres'
    $start.EnvironmentVariables['PGCONNECT_TIMEOUT']='5'
    $start.EnvironmentVariables['PGCLIENTENCODING']='UTF8'
    $start.EnvironmentVariables['PGAPPNAME']='TechMES.PlannedSwitch'
    $start.EnvironmentVariables['LC_ALL']='C'
    $start.EnvironmentVariables['PGOPTIONS']=if ($Change) { '-c statement_timeout=0' } else { '-c statement_timeout=10000' }

    if ($null -ne $Credential) {
        $start.EnvironmentVariables['PGUSER']=$Credential.UserName
        $start.EnvironmentVariables['PGPASSWORD']=$Credential.GetNetworkCredential().Password
    }

    $process=New-Object Diagnostics.Process
    $process.StartInfo=$start
    $errors=New-Object IO.MemoryStream

    try {
        $null=$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.BaseStream.CopyToAsync($errors)

        $process.StandardInput.WriteLine($InputText)
        $process.StandardInput.Close()

        if ($Change) {
            $process.WaitForExit()
        }
        elseif (!$process.WaitForExit(20000)) {
            $process.Kill()
            $process.WaitForExit()
            $null=$stderr.GetAwaiter().GetResult()

            throw ('Read-only check timed out: '+[IO.Path]::GetFileName($File))
        }

        $output=$stdout.GetAwaiter().GetResult().Trim()
        $null=$stderr.GetAwaiter().GetResult()

        try {
            $errorText=(New-Object Text.UTF8Encoding($false,$true)).GetString($errors.ToArray()).Trim()
        }
        catch [Text.DecoderFallbackException] {
            $errorText=[Text.Encoding]::GetEncoding((Get-WinSystemLocale).TextInfo.ANSICodePage).GetString($errors.ToArray()).Trim()
        }

        if ($process.ExitCode -lt 0 -or $process.ExitCode -gt $SuccessCodeMaximum) { throw ([IO.Path]::GetFileName($File)+' failed: '+$errorText+' '+$output) }
        if ($Change -and $output) { Write-Host $output }
        if ($Change -and $errorText) { Write-Host $errorText }

        return $output
    }
    finally {
        $start.EnvironmentVariables.Remove('PGPASSWORD')
        $errors.Dispose()
        $process.Dispose()
    }
}

# Выполняет SQL; пароль передаётся только окружением дочернего процесса.
function Invoke-Sql([string]$Address, [string]$Sql, [switch]$Change) {
    return Invoke-Tool (Join-Path $PgBin 'psql.exe') @('-X','-q','-w','-A','-t','-v','ON_ERROR_STOP=1','-f','-') $Sql $script:SqlCredential $Address -Change:$Change
}

# Читает роль, идентификатор кластера, WAL и пути конфигурации без секретов подключения.
function Get-Node([string]$Address) {
    $sql=@'
BEGIN READ ONLY;
SELECT jsonb_build_object(
 'sid',(SELECT system_identifier::text FROM pg_control_system()),
 'version',current_setting('server_version_num')::integer,
 'data',current_setting('data_directory'),
 'recovery',pg_is_in_recovery(),
 'readOnly',current_setting('default_transaction_read_only')::boolean,
 'timeline',(SELECT timeline_id FROM pg_control_checkpoint()),
 'flush',CASE WHEN NOT pg_is_in_recovery() THEN pg_current_wal_flush_lsn()::text END,
 'receive',pg_last_wal_receive_lsn()::text,
 'replay',pg_last_wal_replay_lsn()::text,
 'paused',CASE WHEN pg_is_in_recovery() THEN pg_is_wal_replay_paused() END,
 'hints',current_setting('wal_log_hints'), 'checksums',current_setting('data_checksums'),
 'fullPages',current_setting('full_page_writes'),
 'syncNames',current_setting('synchronous_standby_names'),
 'walLevel',current_setting('wal_level'),
 'walSendersFree',current_setting('max_wal_senders')::integer-(SELECT count(*) FROM pg_stat_replication),
 'slotsFree',current_setting('max_replication_slots')::integer-(SELECT count(*) FROM pg_replication_slots),
 'standbyMinimums',(SELECT jsonb_object_agg(name,setting::integer) FROM pg_settings
     WHERE name IN ('max_connections','max_prepared_transactions','max_locks_per_transaction','max_wal_senders','max_worker_processes')),
 'tablespaces',(SELECT count(*) FROM pg_tablespace WHERE spcname NOT IN ('pg_default','pg_global')),
 'superuser',(SELECT rolsuper FROM pg_roles WHERE rolname=current_user),
 'configErrors',(SELECT count(*) FROM pg_file_settings WHERE error IS NOT NULL)+(SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL),
 'configs',(SELECT jsonb_agg(DISTINCT path) FROM (
    SELECT sourcefile AS path FROM pg_file_settings
    UNION SELECT file_name FROM pg_hba_file_rules
    UNION SELECT current_setting('config_file') UNION SELECT current_setting('hba_file') UNION SELECT current_setting('ident_file')
    UNION SELECT current_setting(key) FROM unnest(ARRAY['ssl_cert_file','ssl_key_file','ssl_ca_file','ssl_crl_file']) AS key WHERE current_setting('ssl')='on' AND current_setting(key)<>''
 ) AS files WHERE path IS NOT NULL),
 'receiver',COALESCE((SELECT jsonb_agg(jsonb_build_object('host',sender_host,'port',sender_port,'slot',slot_name,'status',status,'timeline',received_tli)) FROM pg_stat_wal_receiver),'[]'::jsonb),
 'replicas',COALESCE((SELECT jsonb_agg(jsonb_build_object('address',host(client_addr),'state',state)) FROM pg_stat_replication),'[]'::jsonb)
);
COMMIT;
'@

    $node=(Invoke-Sql $Address $sql) | ConvertFrom-Json

    if ($ExpectedSystemIdentifier -and $node.sid -cne $ExpectedSystemIdentifier) { throw 'The SQL endpoint belongs to a different PostgreSQL cluster.' }

    return $node
}

# Проверяет действующий вход по физическому протоколу репликации.
function Assert-ReplicationLogin([string]$Address, [string]$SystemIdentifier) {
    $raw=Invoke-Tool (Join-Path $PgBin 'psql.exe') @('-X','-q','-w','-A','-t','-d','replication=1','-c','IDENTIFY_SYSTEM;') '' $script:ReplicationCredential $Address
    $fields=$raw -split '\|'

    if ($fields.Count -ne 4 -or $fields[0] -cne $SystemIdentifier) { throw 'Replication identity does not match the selected cluster.' }
}

# Использует штатный инструмент проверки принадлежности процесса службе.
function Get-LocalService {
    $raw=Invoke-Tool $script:WindowsPowerShell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script:ServiceTool,'-Mode','Status','-AsJson','-ServiceName',$ServiceName,'-PgBin',$PgBin,'-DataDirectory',$DataDirectory,'-Port',[string]$Port)

    return $raw | ConvertFrom-Json
}

# Требует владение работающим PostgreSQL либо его полную остановку.
function Assert-ServiceState([bool]$Running) {
    $state=Get-LocalService

    if ($Running) {
        if (!$state.ManagedByService -or $state.ServiceState -ne 'Running') { throw 'Local PostgreSQL is not managed by its running Windows service.' }
    }
    elseif ($state.ServiceState -ne 'Stopped' -or $state.ListenerProcessId -ne 0 -or $state.PostmasterProcessId -ne 0) {
        throw 'Local PostgreSQL shutdown is not confirmed.'
    }

    return $state
}

# Читает контрольный файл остановленного PostgreSQL в фиксированной локали C.
function Get-Control([string]$Directory, [switch]$AllowUncleanPrimary) {
    $raw=Invoke-Tool (Join-Path $PgBin 'pg_controldata.exe') @('-D',$Directory)
    $values=@{}

    foreach ($line in $raw -split '\r?\n') {
        if ($line -match '^([^:]+):\s*(.*?)\s*$') { $values[$matches[1].Trim()]=$matches[2] }
    }

    foreach ($key in @('Database system identifier','Database cluster state','Latest checkpoint location',"Latest checkpoint's TimeLineID")) {
        if (!$values.ContainsKey($key)) { throw ('Missing pg_controldata field: '+$key) }
    }

    $state=$values['Database cluster state']

    if ($state -ne 'shut down' -and !($AllowUncleanPrimary -and $state -eq 'in production')) { throw 'The source control file does not confirm the expected stopped Primary.' }

    return [pscustomobject]@{
        State=$state
        Sid=$values['Database system identifier']
        Lsn=$values['Latest checkpoint location']
        Timeline=[int]$values["Latest checkpoint's TimeLineID"]
    }
}

# Сохраняет локальный журнал и файлы обмена атомарно.
function Write-Json([string]$Path, $Value) {
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)

    try {
        $acl=Get-Acl -LiteralPath $temporary
        $acl.SetOwner(($administratorsSid))
        Set-Acl -LiteralPath $temporary -AclObject $acl

        $bytes=(New-Object Text.UTF8Encoding($true)).GetBytes(($Value | ConvertTo-Json -Depth 12))
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    # PowerShell преобразует $null в пустую строку; .NET требует настоящий null.
    if ([IO.File]::Exists($Path)) {
        [IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value)
    }
    else {
        [IO.File]::Move($temporary,$Path)
    }
}

# Проверяет, что подтверждение остановки относится к заданной операции и профилю.
function Read-Fence {
    $proof=Get-Content -LiteralPath $FenceFile -Raw | ConvertFrom-Json
    $identifier=[guid]::Parse($proof.Id).ToString('N')
    $format=if ($EmergencyFenceId) { 2 } else { 1 }

    if ($EmergencyFenceId -and $proof.Id -cne $EmergencyFenceId) { throw 'Emergency proof does not match the reserved Fence operation.' }

    if ($identifier -cne $proof.Id -or $proof.Format -ne $format -or $proof.State -ne 'Fenced' -or $proof.From -ne $From -or $proof.To -ne $To -or $proof.ServiceName -ne $ServiceName -or $proof.Port -ne $Port -or (Normalize-Path $proof.DataDirectory) -ine $DataDirectory -or (Normalize-Path $proof.PgBin) -ine $PgBin -or $proof.StartMode -notin @('Auto','Manual') -or $proof.Lsn -notmatch '^[0-9A-F]+/[0-9A-F]+$' -or $proof.Sid -notmatch '^\d+$' -or $proof.Timeline -lt 1) { throw 'Fence file does not match this operation.' }

    if ($ExpectedSystemIdentifier -and $proof.Sid -cne $ExpectedSystemIdentifier) { throw 'The fence proof belongs to a different PostgreSQL cluster.' }

    if ($Mode -eq 'Promote') {
        $age=[DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($proof.CheckedAtUtc)

        if ($age.TotalMinutes -gt 30 -or $age.TotalMinutes -lt -1) { throw 'Fence confirmation must be less than 30 minutes old.' }
    }

    return $proof
}

# Общие SQL-проверки для планового переключения и автоматического возврата.
function Assert-SqlPair($Primary, $Standby) {
    if ($Primary.recovery -or $Primary.readOnly -or !$Standby.recovery -or $Standby.paused -or $Primary.sid -cne $Standby.sid -or $Primary.timeline -ne $Standby.timeline) { throw 'Expected one writable Primary and its unpaused Standby in the same cluster and timeline.' }

    foreach ($node in @($Primary,$Standby)) {
        if ($node.version -lt 180000 -or $node.version -ge 190000 -or $node.version -ne $Primary.version -or !$node.superuser -or $node.configErrors -ne 0 -or $node.tablespaces -ne 0 -or $node.syncNames -ne '' -or (Normalize-Path $node.data) -ine $DataDirectory -or $node.fullPages -ne 'on' -or ($node.hints -ne 'on' -and $node.checksums -ne 'on')) { throw 'Unsupported PostgreSQL settings, permissions or storage layout for this first planned test.' }
    }

    if (@($Standby.receiver | Where-Object { $_.host -eq $From -and $_.port -eq $Port -and $_.status -eq 'streaming' }).Count -ne 1 -or @($Primary.replicas | Where-Object { $_.address -eq $To -and $_.state -eq 'streaming' }).Count -ne 1) { throw 'The selected replication stream is not healthy.' }
}

# Проверяет базовые условия и локальные файлы до остановки Primary.
function Assert-Pair($Primary, $Standby) {
    Assert-SqlPair $Primary $Standby

    $null=Assert-ServiceState $true
    Assert-ReplicationLogin $To $Primary.sid

    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside PGDATA are not supported.' }
}

# Измеряет отставание применения WAL относительно точки flush на текущем Primary.
function Test-FailbackReadiness {
    $primary=Get-Node '127.0.0.1'
    $standby=Get-Node $To
    Assert-SqlPair $primary $standby

    if ($primary.timeline -ne $ExpectedTimeline -or $standby.receiver[0].timeline -ne $ExpectedTimeline -or !$standby.replay -or $primary.flush -notmatch '^[0-9A-F]+/[0-9A-F]+$') { return $false }

    $lsn=$primary.flush
    $lag=$MaxReplayLagBytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    $ready=Invoke-Sql $To "SELECT COALESCE(pg_wal_lsn_diff('$lsn'::pg_lsn,pg_last_wal_replay_lsn()) <= $lag,false) WHERE pg_is_in_recovery() AND NOT pg_is_wal_replay_paused() AND (SELECT timeline_id FROM pg_control_checkpoint()) = $ExpectedTimeline AND (SELECT system_identifier::text FROM pg_control_system()) = '$ExpectedSystemIdentifier';"
    return $ready -ceq 't'
}

# Сравнивает позицию реплики с последним checkpoint остановленного Primary.
function Wait-Replay([string]$Address, [string]$Lsn, [string]$Sid, [int]$Timeline) {
    if ($Lsn -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'Invalid WAL position.' }

    $deadline=[DateTime]::UtcNow.AddSeconds(180)

    do {
        $node=Get-Node $Address

        if (!$node.recovery -or $node.paused -or $node.sid -cne $Sid -or $node.timeline -ne $Timeline) { throw 'Standby identity or role changed while waiting for WAL.' }

        $ready=Invoke-Sql $Address ("SELECT COALESCE(pg_last_wal_replay_lsn()>='"+$Lsn+"'::pg_lsn,false);")

        if ($ready -eq 't') { return }

        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Standby did not replay the final Primary checkpoint. Promotion was not requested.'
}

# Сохраняет слот нового Primary для возвращаемого старого узла.
function Ensure-Slot([string]$Address, [switch]$CheckOnly) {
    $sql="SELECT jsonb_build_object('type',slot_type,'active',active,'status',wal_status) FROM pg_replication_slots WHERE slot_name='$script:Slot';"
    $raw=Invoke-Sql $Address $sql

    if (!$raw) {
        if ($CheckOnly) { return $false }
        $null=Invoke-Sql $Address ("SELECT pg_create_physical_replication_slot('"+$script:Slot+"',true);") -Change
        $raw=Invoke-Sql $Address $sql
    }

    $slot=$raw | ConvertFrom-Json

    if ($slot.type -ne 'physical' -or $slot.active -or $slot.status -in @('lost','unreserved') -or $null -eq $slot.status) { throw 'The rejoin slot is active elsewhere or does not retain the required WAL.' }
    if ($CheckOnly) { return $true }
}

# Экранирует значение строки подключения libpq.
function Quote-Libpq([string]$Value) {
    return "'"+$Value.Replace('\','\\').Replace("'","\'")+"'"
}

# Временно разрешает текущему пользователю rewind копии; затем возвращает исходный DACL.
function Set-WorkingCopyAccess([string]$Stage, [switch]$ForRewind) {
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetSecurityDescriptorSddlForm((Get-Acl -LiteralPath $DataDirectory).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access),[Security.AccessControl.AccessControlSections]::Access)

    if ($ForRewind) {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()

        try {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity.User,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
        }
        finally {
            $identity.Dispose()
        }
    }

    Set-Acl -LiteralPath $Stage -AclObject $acl
}

# Готовит парольный файл только для службы, SYSTEM и администраторов.
function Write-StandbyConfiguration([string]$Stage, $Proof) {
    $password=$script:ReplicationCredential.GetNetworkCredential().Password

    if ($password.IndexOfAny([char[]]"`r`n`0") -ge 0 -or $script:ReplicationCredential.UserName -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Unsupported replication username or password characters for pgpass.' }

    $passName='techmes-replication.pgpass'
    $passPath=Join-Path $Stage $passName
    $finalPassPath=(Join-Path $DataDirectory $passName).Replace('\','/')
    $escapedPassword=$password.Replace('\','\\').Replace(':','\:')
    $line=$To+':'+$Port+':replication:'+$script:ReplicationCredential.UserName+':'+$escapedPassword+"`n"

    [IO.File]::WriteAllBytes($passPath,[byte[]]@())

    $service=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $account=if ($service.StartName -eq 'LocalSystem') {
        $localSystemSid
    }
    else {
        (New-Object Security.Principal.NTAccount($service.StartName)).Translate([Security.Principal.SecurityIdentifier])
    }

    $acl=New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true,$false)

    foreach ($sid in @($administratorsSid.Value,$localSystemSid.Value)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','Allow')))
    }

    if ($account.Value -notin @($localSystemSid.Value,$administratorsSid.Value)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account,'Read','Allow')))
    }

    Set-Acl -LiteralPath $passPath -AclObject $acl

    [IO.File]::WriteAllText($passPath,$line,(New-Object Text.UTF8Encoding($false)))

    $connection='host='+(Quote-Libpq $To)+' port='+$Port+' user='+(Quote-Libpq $script:ReplicationCredential.UserName)+' passfile='+(Quote-Libpq $finalPassPath)+' application_name='+(Quote-Libpq $script:Slot)+' connect_timeout=5'
    $configuration="`r`n# TechMES standby "+$Proof.Id+"`r`nprimary_conninfo = '"+$connection.Replace('\','\\').Replace("'","''")+"'`r`nprimary_slot_name = '"+$script:Slot+"'`r`nrecovery_target_timeline = 'latest'`r`n"

    foreach ($setting in @('recovery_target','recovery_target_name','recovery_target_time','recovery_target_xid','recovery_target_lsn','restore_command')) {
        $configuration+=$setting+" = ''`r`n"
    }

    $configuration+="recovery_target_action = 'pause'`r`n"

    $stream=[IO.File]::Open((Join-Path $Stage 'postgresql.auto.conf'),[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)

    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes($configuration)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    [IO.File]::WriteAllBytes((Join-Path $Stage 'standby.signal'),[byte[]]@())

    if (Test-Path -LiteralPath (Join-Path $Stage 'recovery.signal')) { throw 'Unexpected recovery.signal in the prepared directory.' }
}

# Проверяет устойчивую работу возвращённого узла в режиме реплики.
function Wait-Standby($Proof, [int]$ExpectedTimeline=0, [string]$RequiredReplay='') {
    if ($RequiredReplay -and $RequiredReplay -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'Invalid required replay LSN.' }

    $deadline=[DateTime]::UtcNow.AddSeconds(180)
    $good=0

    do {
        $node=$null

        try {
            $null=Assert-ServiceState $true
            $node=Get-Node '127.0.0.1'
        }
        catch {
            $good=0
            Write-Host ('Waiting for Standby startup: '+$_.Exception.Message)
        }

        if ($null -eq $node) {
            Start-Sleep -Seconds 10
            continue
        }

        if (!$node.recovery -or $node.sid -cne $Proof.Sid) { throw 'The returning node is not a Standby of the expected cluster.' }

        $streaming=@($node.receiver | Where-Object { $_.host -eq $To -and $_.port -eq $Port -and $_.slot -eq $script:Slot -and $_.status -eq 'streaming' -and (!$ExpectedTimeline -or $_.timeline -eq $ExpectedTimeline) }).Count -eq 1
        $caughtUp=!$RequiredReplay
        if ($RequiredReplay -and $streaming -and !$node.paused) {
            try {
                $caughtUp=(Invoke-Sql '127.0.0.1' "SELECT COALESCE(pg_last_wal_replay_lsn() >= '$RequiredReplay'::pg_lsn,false);") -eq 't'
            }
            catch {
                Write-Host 'Waiting for confirmation of the required replay LSN.'
            }
        }

        if ($streaming -and !$node.paused -and $caughtUp) {
            $good++
        }
        else {
            $good=0
        }

        if ($good -ge 7) { return $node }

        Start-Sleep -Seconds 10
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The returning Standby did not confirm stable streaming replication.'
}

# Останавливает прежний Primary, запрещает запуск службы и оставляет standby.signal.
function Invoke-Fence {
    if ($script:LocalAddress -ne $From) { throw 'Fence must run on the old Primary.' }
    if ((Test-Path -LiteralPath $FenceFile) -or (Test-Path -LiteralPath $script:ActiveFencePath)) { throw 'An exchange file or unfinished local fence already exists. Inspect it before starting another switch.' }

    $primary=Get-Node '127.0.0.1'
    $standby=Get-Node $To

    Assert-Pair $primary $standby

    if ((Test-Path -LiteralPath (Join-Path $DataDirectory 'standby.signal')) -or (Test-Path -LiteralPath (Join-Path $DataDirectory 'recovery.signal'))) { throw 'Unexpected recovery signal on the current Primary.' }

    $configs=@(@($primary.configs)+@((Join-Path $DataDirectory 'postgresql.auto.conf')) | ForEach-Object { Get-ConfigRelativePath $_ } | Sort-Object -Unique)
    $service=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"

    if ($service.StartMode -notin @('Auto','Manual')) { throw 'The current service startup type is not Auto or Manual.' }

    $size=(Get-ChildItem -LiteralPath $DataDirectory -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum

    if ((New-Object IO.DriveInfo([IO.Path]::GetPathRoot($DataDirectory))).AvailableFreeSpace -lt ($size+2GB)) { throw 'The first test needs space for one working copy of PGDATA plus 2 GB.' }

    $proof=[pscustomobject]@{
        Format=1
        Id=[guid]::NewGuid().ToString('N')
        State='Fencing'
        CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        From=$From
        To=$To
        ServiceName=$ServiceName
        PgBin=$PgBin
        DataDirectory=$DataDirectory
        Port=$Port
        StartMode=$service.StartMode
        Sid=$primary.sid
        Timeline=$primary.timeline
        Lsn=''
        Configs=$configs
    }

    Write-Json $script:ActiveFencePath $proof

    $script:Changed=$true

    Set-Service -Name $ServiceName -StartupType Disabled
    Stop-Service -Name $ServiceName -ErrorAction Stop
    (Get-Service -Name $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(300))

    $null=Assert-ServiceState $false

    [IO.File]::WriteAllBytes((Join-Path $DataDirectory 'standby.signal'),[byte[]]@())

    $control=Get-Control $DataDirectory

    if ($control.Sid -cne $primary.sid -or $control.Timeline -ne $primary.timeline) { throw 'Offline cluster identity changed during shutdown.' }
    if ((Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode -ne 'Disabled') { throw 'The old Primary service is not disabled.' }

    $proof.State='Fenced'
    $proof.Lsn=$control.Lsn
    $proof.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')

    Write-Json $script:ActiveFencePath $proof
    Write-Json $FenceFile $proof

    return [pscustomobject]@{
        State='Fenced'
        ExchangeFile=$FenceFile
        FinalCheckpoint=$proof.Lsn
        Next=('Copy the exchange file to '+$To+' and run Promote.')
    }
}

# Проверяет фактическую остановку, включая оставшиеся процессы и TCP-порт.
function Assert-EmergencyStopped {
    $state=Assert-ServiceState $false
    $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $Port)
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='postgres.exe'" -ErrorAction Stop | Where-Object {
        !$_.ExecutablePath -or (Normalize-Path $_.ExecutablePath) -ieq (Join-Path $PgBin 'postgres.exe')
    })

    if ($state.ServiceProcessId -ne 0 -or $listeners.Count -ne 0 -or $processes.Count -ne 0) { throw 'PostgreSQL is not confirmed stopped or a PostgreSQL process cannot be identified.' }
}

# Изолирует уже остановленный Primary без SQL-запроса к нему.
function Invoke-EmergencyFence {
    if ($script:LocalAddress -ne $From -or $OperationId -cne $EmergencyFenceId) { throw 'Emergency Fence must run on the reserved source.' }
    if ((Test-Path -LiteralPath $FenceFile) -or (Test-Path -LiteralPath $script:ActiveFencePath)) { throw 'A fence already exists. Inspect the saved operation.' }

    Assert-EmergencyStopped

    $before=Get-Control $DataDirectory -AllowUncleanPrimary
    $standby=Get-Node $To
    $service=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    $signal=Join-Path $DataDirectory 'standby.signal'

    if ($before.Sid -cne $ExpectedSystemIdentifier -or $standby.sid -cne $before.Sid -or $standby.timeline -ne $before.Timeline -or !$standby.recovery -or $standby.paused -or $standby.receive -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'The standby cannot confirm the source cluster, timeline or received WAL.' }
    if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -notin @('Auto','Manual')) { throw 'The original service must be stopped with a known startup type.' }
    if ((Test-Path -LiteralPath $signal) -or (Test-Path -LiteralPath (Join-Path $DataDirectory 'recovery.signal'))) { throw 'Unexpected recovery signal on the old Primary.' }

    $proof=[pscustomobject]@{
        Format=2
        Id=$EmergencyFenceId
        State='Fencing'
        CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        From=$From
        To=$To
        ServiceName=$ServiceName
        PgBin=$PgBin
        DataDirectory=$DataDirectory
        Port=$Port
        StartMode=$service.StartMode
        Sid=$before.Sid
        Timeline=$before.Timeline
        Lsn=$before.Lsn
        ClusterState=$before.State
    }

    Write-Json $script:ActiveFencePath $proof
    $script:Changed=$true

    Set-Service -Name $ServiceName -StartupType Disabled -ErrorAction Stop
    Assert-EmergencyStopped

    $after=Get-Control $DataDirectory -AllowUncleanPrimary
    $disabled=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop

    if ($null -eq $disabled -or $disabled.StartMode -ne 'Disabled' -or $after.Sid -cne $before.Sid -or $after.Timeline -ne $before.Timeline -or $after.Lsn -cne $before.Lsn -or $after.State -ne $before.State) { throw 'Persistent isolation or the original control file was not confirmed.' }

    # Если старый PGDATA запустят вне службы, этот флаг требует режима восстановления.
    [IO.File]::WriteAllBytes($signal,[byte[]]@())

    $proof.State='Fenced'
    $proof.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Write-Json $script:ActiveFencePath $proof
    Write-Json $FenceFile $proof

    return [pscustomobject]@{
        State='Fenced'
        ExchangeFile=$FenceFile
        SourceCheckpoint=$before.Lsn
        ClusterState=$before.State
    }
}

# Повышает догнавшую реплику после подтверждённой остановки старого Primary.
function Invoke-Promote {
    if ($script:LocalAddress -ne $To) { throw 'Promote must run on the selected new Primary.' }

    $proof=Read-Fence
    $null=Assert-ServiceState $true
    $node=Get-Node '127.0.0.1'

    if ($node.sid -cne $proof.Sid -or $node.readOnly -or $node.version -lt 180000 -or $node.version -ge 190000) { throw 'The new Primary identity or settings do not match.' }

    $journalPath=Join-Path $script:Root ('promotion-'+$proof.Id+'.json')
    $oldResponded=$false

    try {
        $null=Get-Node $From
        $oldResponded=$true
    }
    catch {
        Write-Host 'Old Primary SQL endpoint is not responding; the exchange file and persistent local fencing are required.'
    }

    if ($oldResponded) { throw 'The old PostgreSQL endpoint is still responding. Inspect its state before promotion.' }

    if ($node.recovery) {
        if (Test-Path -LiteralPath $journalPath) { throw 'A promotion was already requested. Inspect its journal before issuing another request.' }

        # При аварии ждём применение WAL, полученного репликой до повышения.
        $requiredLsn=if ($EmergencyFenceId) { $node.receive } else { $proof.Lsn }
        Wait-Replay '127.0.0.1' $requiredLsn $proof.Sid $proof.Timeline

        $null=Assert-ServiceState $true

        Write-Json $journalPath ([pscustomobject]@{ Id=$proof.Id; State='PromotionRequested'; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })

        $script:Changed=$true

        $null=Invoke-Sql '127.0.0.1' 'SELECT pg_promote(true,60);' -Change
        $node=Get-Node '127.0.0.1'
    }
    elseif (!(Test-Path -LiteralPath $journalPath)) {
        throw 'This node was promoted outside this operation; automatic continuation is refused.'
    }

    if ($node.recovery -or $node.readOnly -or $node.sid -cne $proof.Sid) { throw 'Promotion is not confirmed. The old node must remain fenced.' }

    $script:Changed=$true

    Ensure-Slot '127.0.0.1'

    $null=Invoke-Sql '127.0.0.1' 'CHECKPOINT;' -Change
    $node=Get-Node '127.0.0.1'

    if ($node.recovery -or $node.readOnly -or $node.sid -cne $proof.Sid -or $node.timeline -le $proof.Timeline) { throw 'The new Primary timeline is not confirmed after checkpoint. The old node must remain fenced.' }

    Write-Json $journalPath ([pscustomobject]@{ Id=$proof.Id; State='Promoted'; Timeline=$node.timeline; Slot=$script:Slot; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })

    return [pscustomobject]@{
        State='Promoted'
        Primary=$To
        Timeline=$node.timeline
        Slot=$script:Slot
        Next=('Run Rejoin on '+$From+' using the same exchange file.')
    }
}

# У аварийного Fence нет списка файлов, снятого через SQL до остановки.
# -C должен стоять первым для чтения параметров под учётной записью контроллера.
function Get-EmergencyRejoinConfigs {
    $postgres=Join-Path $PgBin 'postgres.exe'
    $service=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    if ($null -eq $service -or $service.PathName -match '(?:^|\s)-o(?:\s|"|$)') { throw 'Cannot reconstruct configuration with PostgreSQL service option overrides.' }

    $directory=Invoke-Tool $postgres @('-C','data_directory','-D',$DataDirectory)
    if ((Normalize-Path $directory) -ine $DataDirectory) { throw 'The offline PostgreSQL configuration points to a different PGDATA.' }

    $paths=@('postgresql.auto.conf')
    foreach ($setting in @('config_file','hba_file','ident_file')) {
        $path=Invoke-Tool $postgres @('-C',$setting,'-D',$DataDirectory)
        if (!$path) { throw ('Cannot identify offline PostgreSQL setting: '+$setting) }
        $paths+=Get-ConfigRelativePath $path
    }

    foreach ($relative in $paths) {
        $path=Join-Path $DataDirectory $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Local configuration file is missing: '+$relative) }
        if (Select-String -LiteralPath $path -Pattern '^\s*include(?:_dir|_if_exists)?(?:\s|=)' -Quiet -ErrorAction Stop) {
            throw 'Included configuration files require a verified inventory. This operation cannot preserve them.'
        }
    }

    if ((Invoke-Tool $postgres @('-C','ssl','-D',$DataDirectory)) -eq 'on') {
        if (Invoke-Tool $postgres @('-C','ssl_crl_dir','-D',$DataDirectory)) { throw 'Cannot preserve the local SSL CRL directory from this emergency Fence.' }
        foreach ($setting in @('ssl_cert_file','ssl_key_file','ssl_ca_file','ssl_crl_file')) {
            $path=Invoke-Tool $postgres @('-C',$setting,'-D',$DataDirectory)
            if ($path) {
                $relative=Get-ConfigRelativePath $path
                if (!(Test-Path -LiteralPath (Join-Path $DataDirectory $relative) -PathType Leaf)) { throw ('Local SSL file is missing: '+$relative) }
                $paths+=$relative
            }
        }
    }

    return $paths | Sort-Object -Unique
}

# Подтверждает совпадение локального Fence и состояние прежнего Primary.
function Read-RecoveryFence {
    if ($script:LocalAddress -ne $From) { throw 'Standby recovery must run on the old Primary.' }

    $proof=Read-Fence
    Assert-LocalPath $script:ActiveFencePath
    $local=Get-Content -LiteralPath $script:ActiveFencePath -Raw | ConvertFrom-Json

    if ($local.Id -cne $proof.Id -or $local.State -ne 'Fenced') { throw 'The local fence marker does not match this operation.' }

    if ($EmergencyFenceId -and ($local.Format -ne 2 -or $local.Sid -cne $proof.Sid -or $local.From -cne $From -or $local.To -cne $To -or $local.Lsn -cne $proof.Lsn -or $local.Timeline -ne $proof.Timeline -or $local.StartMode -ne $proof.StartMode -or $local.ClusterState -ne $proof.ClusterState -or $proof.ClusterState -notin @('shut down','in production'))) {
        throw 'Emergency recovery requires the unchanged local Fence.'
    }

    Assert-RecoveryServiceStopped
    $control=Get-Control $DataDirectory -AllowUncleanPrimary:([bool]$EmergencyFenceId)
    if ($control.Sid -cne $proof.Sid -or $control.Lsn -cne $proof.Lsn -or $control.Timeline -ne $proof.Timeline -or ($EmergencyFenceId -and $control.State -ne $proof.ClusterState)) { throw 'The old data directory changed after fencing.' }

    return $proof
}

function Assert-RecoveryServiceStopped {
    if ($EmergencyFenceId) {
        Assert-EmergencyStopped
    }
    else {
        $null=Assert-ServiceState $false
    }

    if ((Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode -ne 'Disabled' -or !(Test-Path -LiteralPath (Join-Path $DataDirectory 'standby.signal') -PathType Leaf)) { throw 'The old node must remain stopped, disabled and marked as Standby.' }
}

function Get-RecoveryPrimary($Proof) {
    $primary=Get-Node $To
    if ($primary.recovery -or $primary.readOnly -or $primary.sid -cne $Proof.Sid -or $primary.timeline -le $Proof.Timeline) { throw 'The selected new Primary is not confirmed.' }

    if ($EmergencyFenceId -and ($primary.version -lt 180000 -or $primary.version -ge 190000 -or !$primary.superuser -or $primary.tablespaces -ne 0 -or $primary.configErrors -ne 0)) {
        throw 'The current Primary configuration is not supported by emergency recovery.'
    }

    return $primary
}

# Возвращает старый узел через pg_rewind рабочей копии; исходный каталог сохраняется.
function Invoke-Rejoin {
    $proof=Read-RecoveryFence
    if ($EmergencyFenceId -and $proof.ClusterState -ne 'shut down') {
        $script:RebuildRequired=$true
        throw 'Emergency Rejoin requires a clean shutdown. Use Rebuild for this emergency plan.'
    }

    $primary=Get-RecoveryPrimary $proof
    Assert-ReplicationLogin $To $proof.Sid

    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside PGDATA are not supported.' }

    $configs=if ($EmergencyFenceId) { @(Get-EmergencyRejoinConfigs) } else { @($proof.Configs) }

    if ($EmergencyFenceId) {
        $size=(Get-ChildItem -LiteralPath $DataDirectory -Recurse -File -Force | Measure-Object Length -Sum).Sum
        if ((New-Object IO.DriveInfo([IO.Path]::GetPathRoot($DataDirectory))).AvailableFreeSpace -lt ($size+2GB)) { throw 'Insufficient space for a working copy of PGDATA plus 2 GB.' }
    }

    $stage=$DataDirectory+'.techmes-stage-'+$proof.Id
    $backup=$DataDirectory+'.techmes-before-'+$proof.Id

    if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) { throw 'A working copy or backup already exists. Inspect the previous run before retrying.' }

    Ensure-Slot $To

    $script:Changed=$true

    $null=New-Item -ItemType Directory -Path $stage

    Set-WorkingCopyAccess $stage -ForRewind

    $null=Invoke-Tool (Join-Path $env:SystemRoot 'System32\robocopy.exe') @($DataDirectory,$stage,'/E','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/XJ','/NFL','/NDL') -Change -SuccessCodeMaximum 7

    $stageControl=Get-Control $stage

    if ($stageControl.Sid -cne $proof.Sid -or $stageControl.Lsn -cne $proof.Lsn) { throw 'Working copy control data does not match the original.' }

    $source='host='+(Quote-Libpq $To)+' port='+$Port+' dbname=postgres user='+(Quote-Libpq $script:SqlCredential.UserName)+' connect_timeout=5'
    $rewindArguments=@(('--target-pgdata='+$stage),('--source-server='+$source),'--no-ensure-shutdown','--progress')

    try {
        $null=Invoke-Tool (Join-Path $PgBin 'pg_rewind.exe') ($rewindArguments+@('--dry-run')) '' $script:SqlCredential $To -Change
        $null=Invoke-Tool (Join-Path $PgBin 'pg_rewind.exe') $rewindArguments '' $script:SqlCredential $To -Change
    }
    catch {
        if ($EmergencyFenceId) { $script:RebuildRequired=$true }
        throw
    }

    Restore-StandbyConfigs $stage $configs
    $expectedTimeline=if ($EmergencyFenceId) { $primary.timeline } else { 0 }
    return Install-StandbyCopy $stage $backup $proof $expectedTimeline
}

# Возвращает в рабочую копию локальную конфигурацию и TLS-материал.
function Restore-StandbyConfigs([string]$Stage, [string[]]$Configs) {
    foreach ($relative in $Configs) {
        $relative=Get-ConfigRelativePath ([string]$relative)
        $original=Join-Path $DataDirectory $relative

        if (Test-Path -LiteralPath $original -PathType Leaf) {
            $target=Join-Path $Stage $relative
            $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            [IO.File]::Copy($original,$target,$true)
        }
    }
}

# Проверяет отключённый новый экземпляр и его исходный идентификатор.
function Get-InitialTarget([string]$TargetSid='') {
    if ($script:LocalAddress -ne $From) { throw 'Run locally on the new Standby computer.' }
    if (Test-Path -LiteralPath $script:ActiveFencePath) { throw 'The target has an active Fence. Finish the existing switch.' }

    $network=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$ServiceName.ToUpperInvariant()+'\Commands\Network')
    if ((Test-Path -LiteralPath (Join-Path $network 'active-switch.json')) -or (Test-Path -LiteralPath (Join-Path $network 'console-active.json'))) {
        throw 'The target has an active switch plan.'
    }
    $null=Assert-ServiceState $false
    $service=Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled') { throw 'The PostgreSQL service on the new Standby must be stopped and Disabled.' }
    if ((Get-Content -LiteralPath (Join-Path $DataDirectory 'PG_VERSION') -Raw).Trim() -cne '18') { throw 'The target must contain an initialized PostgreSQL 18 data directory.' }
    foreach ($file in @('standby.signal','recovery.signal','postmaster.pid')) {
        if (Test-Path -LiteralPath (Join-Path $DataDirectory $file)) { throw ('Unexpected file in the new target: '+$file) }
    }
    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside target PGDATA are not supported.' }

    $control=Get-Control $DataDirectory
    if ($control.Sid -notmatch '^[0-9]{1,20}$' -or $control.Sid -ceq $ExpectedSystemIdentifier -or ($TargetSid -and $control.Sid -cne $TargetSid)) {
        throw 'The target identity is not the separate PostgreSQL cluster selected for first Standby creation.'
    }
    return $control
}

# Подтверждает доступ к Primary и физическому протоколу репликации до копирования.
function Get-InitialPrimary {
    $primary=Get-Node $To
    if ($primary.recovery -or $primary.readOnly -or $primary.version -lt 180000 -or $primary.version -ge 190000 -or !$primary.superuser -or $primary.tablespaces -ne 0 -or $primary.configErrors -ne 0 -or $primary.timeline -le 0 -or $primary.flush -notmatch '^[0-9A-F]+/[0-9A-F]+$') {
        throw 'The selected source must be a writable PostgreSQL 18 Primary with valid configuration and no custom tablespaces.'
    }
    if (@($primary.replicas).Count -gt 0 -or $primary.walLevel -notin @('replica','logical') -or $primary.walSendersFree -lt 2) {
        throw 'First Standby creation requires a source without connected replicas, wal_level=replica/logical and two free WAL senders.'
    }
    if (!(Ensure-Slot $To -CheckOnly) -and $primary.slotsFree -lt 1) { throw 'The Primary has no free physical replication slot.' }
    Assert-ReplicationLogin $To $ExpectedSystemIdentifier
    return $primary
}

function Get-BaseBackupSize {
    $estimate=Invoke-Sql $To 'SELECT COALESCE(sum(pg_database_size(oid)),0)::bigint + COALESCE((SELECT sum(size) FROM pg_ls_waldir()),0)::bigint FROM pg_database;'
    $bytes=0L
    if (![long]::TryParse($estimate,[ref]$bytes) -or $bytes -le 0) { throw 'Cannot estimate the size of the current PostgreSQL cluster.' }
    if ((New-Object IO.DriveInfo([IO.Path]::GetPathRoot($DataDirectory))).AvailableFreeSpace -lt ($bytes+2GB)) { throw 'Insufficient space for a base backup plus 2 GB.' }
    return $bytes
}

function Copy-VerifiedBaseBackup([string]$Stage, [string]$Backup, [string]$Journal, [string]$Sid, [int]$Timeline) {
    Write-Json $Journal ([pscustomobject]@{ State='BackingUp'; Stage=$Stage; Backup=$Backup; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    $script:Changed=$true
    $null=New-Item -ItemType Directory -Path $Stage
    Set-WorkingCopyAccess $Stage -ForRewind

    Write-Host ('Copying Primary '+$To+' using slot '+$script:Slot+' into '+$Stage)
    $arguments=@(('--pgdata='+$Stage),'--format=plain','--wal-method=stream',('--slot='+$script:Slot),'--no-password','--no-clean')
    $null=Invoke-Tool (Join-Path $PgBin 'pg_basebackup.exe') $arguments '' $script:ReplicationCredential $To -Change
    Write-Json $Journal ([pscustomobject]@{ State='Verifying'; Stage=$Stage; Backup=$Backup; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    $null=Invoke-Tool (Join-Path $PgBin 'pg_verifybackup.exe') @($Stage) -Change

    $copy=Get-Control $Stage -AllowUncleanPrimary
    if ($copy.Sid -cne $Sid -or $copy.Timeline -ne $Timeline) { throw 'The base backup belongs to a different cluster or timeline.' }
}

function Invoke-InitialStandby([switch]$CheckOnly) {
    $target=Get-InitialTarget $ExpectedTargetSystemIdentifier
    $primary=Get-InitialPrimary
    $configs=@(Get-EmergencyRejoinConfigs)
    $postgres=Join-Path $PgBin 'postgres.exe'
    if ((Invoke-Tool $postgres @('-C','hot_standby','-D',$DataDirectory)) -ne 'on' -or (Invoke-Tool $postgres @('-C','port','-D',$DataDirectory)) -ne [string]$Port) {
        throw 'Enable hot_standby and match the configured port on the new target.'
    }
    foreach ($minimum in $primary.standbyMinimums.PSObject.Properties) {
        $setting=Invoke-Tool $postgres @('-C',$minimum.Name,'-D',$DataDirectory)
        if ($setting -notmatch '^[0-9]+$' -or [int]$setting -lt $minimum.Value) { throw ('The target must match or exceed the Primary setting '+$minimum.Name+'.') }
    }
    $bytes=Get-BaseBackupSize
    if ($CheckOnly) {
        return [pscustomobject]@{ State='Ready'; Primary=$To; Standby=$From; SystemIdentifier=$primary.sid; TargetSystemIdentifier=$target.Sid; Slot=$script:Slot; EstimatedBytes=$bytes }
    }

    $proof=[pscustomobject]@{ Id=$OperationId; Sid=$primary.sid; StartMode='Auto'; TargetSid=$target.Sid; TargetCheckpoint=$target.Lsn }
    $stage=$DataDirectory+'.techmes-initial-stage-'+$proof.Id
    $backup=$DataDirectory+'.techmes-before-'+$proof.Id
    Assert-LocalPath $stage
    Assert-LocalPath $backup
    if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) { throw 'A working copy or original backup already exists for this run.' }

    $script:InitialCopy=[pscustomobject]@{ State='Initializing'; Stage=$stage; Backup=$backup; Slot=$script:Slot; TargetSystemIdentifier=$target.Sid }
    $journal=Join-Path $script:Root ('initial-'+$proof.Id+'.json')
    Ensure-Slot $To
    Copy-VerifiedBaseBackup $stage $backup $journal $proof.Sid $primary.timeline
    $confirmed=Get-Node $To
    if ($confirmed.recovery -or $confirmed.readOnly -or $confirmed.timeline -ne $primary.timeline -or $confirmed.flush -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'The source role or timeline changed during the base backup.' }

    Restore-StandbyConfigs $stage $configs
    return Install-StandbyCopy $stage $backup $proof $confirmed.timeline $confirmed.flush
}

# Устанавливает готовую копию и снимает Fence после проверки реплики.
function Install-StandbyCopy([string]$Stage, [string]$Backup, $Proof, [int]$ExpectedTimeline, [string]$RequiredReplay='') {
    Write-StandbyConfiguration $Stage $Proof
    Set-WorkingCopyAccess $Stage
    Ensure-Slot $To
    if ($Mode -eq 'InitializeStandby') {
        $target=Get-InitialTarget $Proof.TargetSid
        if ($target.Lsn -cne $Proof.TargetCheckpoint) { throw 'The target checkpoint changed during initial copy.' }
    }
    else { Assert-RecoveryServiceStopped }

    $journal=Join-Path $script:Root ($Mode.ToLowerInvariant()+'-'+$Proof.Id+'.json')
    Write-Json $journal ([pscustomobject]@{ State='Installing'; Stage=$Stage; Backup=$Backup; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })

    [IO.Directory]::Move($DataDirectory,$Backup)
    try {
        [IO.Directory]::Move($Stage,$DataDirectory)
    }
    catch {
        if (!(Test-Path -LiteralPath $DataDirectory)) { [IO.Directory]::Move($Backup,$DataDirectory) }
        throw
    }

    $script:Installed=$true
    $startup=if ($Proof.StartMode -eq 'Auto') { 'Automatic' } else { 'Manual' }

    Set-Service -Name $ServiceName -StartupType $startup
    Start-Service -Name $ServiceName -ErrorAction Stop

    (Get-Service -Name $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(300))

    $node=Wait-Standby $Proof $ExpectedTimeline $RequiredReplay
    $state=switch ($Mode) { 'Rebuild' { 'Rebuilt' }; 'InitializeStandby' { 'Initialized' }; default { 'Rejoined' } }

    Write-Json $journal ([pscustomobject]@{ State=$state; Backup=$Backup; RequiredReplay=$RequiredReplay; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    if ($Mode -ne 'InitializeStandby') { Remove-Item -LiteralPath $script:ActiveFencePath }

    return [pscustomobject]@{
        State=$state
        Primary=$To
        Standby=$From
        Backup=$Backup
        Receiver=$node.receiver
        Next='Verify application database routing and retain the backup.'
    }
}

# Получает и проверяет полный снимок текущего Primary в отдельном каталоге.
function Invoke-Rebuild {
    $proof=Read-RecoveryFence
    $primary=Get-RecoveryPrimary $proof
    Assert-ReplicationLogin $To $proof.Sid
    $configs=@(Get-EmergencyRejoinConfigs)

    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside PGDATA are not supported.' }

    $stage=$DataDirectory+'.techmes-rebuild-stage-'+$proof.Id
    $backup=$DataDirectory+'.techmes-before-'+$proof.Id
    Assert-LocalPath $stage
    Assert-LocalPath $backup
    if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) { throw 'A Rebuild working copy or original backup already exists. Inspect the previous run.' }

    $null=Get-BaseBackupSize
    Ensure-Slot $To
    $journal=Join-Path $script:Root ('rebuild-'+$proof.Id+'.json')
    Copy-VerifiedBaseBackup $stage $backup $journal $proof.Sid $primary.timeline
    $confirmed=Get-RecoveryPrimary $proof
    if ($confirmed.timeline -ne $primary.timeline -or $confirmed.flush -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'Cannot confirm the current Primary and its WAL position after backup.' }
    $null=Read-RecoveryFence

    Restore-StandbyConfigs $stage $configs
    return Install-StandbyCopy $stage $backup $proof $confirmed.timeline $confirmed.flush
}

# Получает DPAPI-блок через stdin; пароли не попадают в аргументы и файлы запроса.
function Read-ProtectedCredentials {
    $plain=$null

    try {
        Add-Type -AssemblyName System.Security
        $line=[Console]::In.ReadLine()

        if (!$line -or $line.Length -gt 96000) { throw 'Invalid protected input.' }

        $entropy=[Text.Encoding]::UTF8.GetBytes('TechMES.PostgreSql.Commands.v1')
        $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($line),$entropy,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        $value=[Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json

        if ($value.sqlUser -notmatch '^[A-Za-z0-9_.-]+$' -or $value.replicationUser -notmatch '^[A-Za-z0-9_.-]+$' -or !$value.sqlPassword -or !$value.replicationPassword) { throw 'Invalid credentials.' }

        $script:SqlCredential=New-Object Management.Automation.PSCredential($value.sqlUser,(ConvertTo-SecureString $value.sqlPassword -AsPlainText -Force))
        $script:ReplicationCredential=New-Object Management.Automation.PSCredential($value.replicationUser,(ConvertTo-SecureString $value.replicationPassword -AsPlainText -Force))
    }
    catch {
        throw 'Could not read the protected local PostgreSQL credentials.'
    }
    finally {
        if ($null -ne $plain) { [Array]::Clear($plain,0,$plain.Length) }
    }
}

# Требует локальный запуск с административными правами Windows.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run on a Windows PostgreSQL server.' }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
}
finally {
    $identity.Dispose()
}

if ($From -eq $To) { throw 'From and To must be different database nodes.' }

$PgBin=Normalize-Path $PgBin
$DataDirectory=Normalize-Path $DataDirectory

Assert-LocalPath $PgBin
Assert-LocalPath $DataDirectory

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
$local=@($From,$To | Where-Object { $_ -in $addresses })

if ($local.Count -ne 1) { throw 'Run locally on exactly one selected database node.' }

$script:LocalAddress=$local[0]

if ($Mode -eq 'FailbackReadiness') {
    if (!$CredentialInput -or !$ExpectedSystemIdentifier -or $ExpectedTimeline -le 0 -or $script:LocalAddress -ne $From) { throw 'Failback readiness requires protected credentials and the local source controller.' }

    Read-ProtectedCredentials
    if (Test-FailbackReadiness) { 'READY' } else { 'WAITING' }
    return
}

$script:Slot=if ([StringComparer]::Ordinal.Compare($From,$To) -gt 0) { 'techmes_primary' } else { 'techmes_secondary' }
$script:WindowsPowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:ServiceTool=Join-Path $PSScriptRoot 'Set-PostgreSQL18ServiceControl.ps1'
$initial=$Mode -in @('CheckInitialStandby','InitializeStandby')
if ($initial -and ($CredentialInput -or $EmergencyFenceId -or !$ExpectedSystemIdentifier -or $script:LocalAddress -ne $From)) {
    throw 'First Standby creation requires interactive use on the target and the expected source cluster identifier.'
}
if ($initial -and $Mode -eq 'InitializeStandby' -and (!$ExpectedTargetSystemIdentifier -or !$OperationId)) {
    throw 'Run the preflight and specify its target cluster identifier and a new operation ID.'
}

$tools=@('psql.exe','pg_rewind.exe','pg_controldata.exe')
if ($Mode -eq 'Rebuild') { $tools=@('psql.exe','pg_controldata.exe','pg_basebackup.exe','pg_verifybackup.exe') }
if ($initial) { $tools=@('psql.exe','postgres.exe','pg_controldata.exe','pg_basebackup.exe','pg_verifybackup.exe') }
foreach ($file in $tools) {
    if (!(Test-Path -LiteralPath (Join-Path $PgBin $file) -PathType Leaf)) { throw ('Required tool missing: '+$file) }
}

if (!(Test-Path -LiteralPath $script:ServiceTool -PathType Leaf)) { throw 'Place Set-PostgreSQL18ServiceControl.ps1 beside this script.' }

if ($Mode -ne 'Status' -and !$initial) {
    if (!$FenceFile) { throw 'Specify an exchange file using -FenceFile.' }

    $FenceFile=[IO.Path]::GetFullPath($FenceFile)

    Assert-LocalPath $FenceFile

    if ($FenceFile.StartsWith($DataDirectory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The exchange file must be outside PGDATA.' }
    if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($FenceFile)) -PathType Container)) { throw 'Create the exchange directory before running this script.' }
}

if ($CredentialInput -and (!$OperationId -or !$ExpectedSystemIdentifier -or !$ResultFile)) { throw 'Non-interactive execution requires OperationId, ExpectedSystemIdentifier and ResultFile.' }
if (($Mode -eq 'Rebuild' -and !$EmergencyFenceId) -or ($EmergencyFenceId -and (!$CredentialInput -or $Mode -notin @('Fence','Promote','Rejoin','Rebuild')))) { throw 'Emergency phases require the TechMES controller and a reserved operation.' }

if ($ResultFile) {
    $ResultFile=[IO.Path]::GetFullPath($ResultFile)

    Assert-LocalPath $ResultFile

    if ($ResultFile.StartsWith($DataDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or $ResultFile -ieq $FenceFile -or (Test-Path -LiteralPath $ResultFile)) { throw 'Use a new result file outside PGDATA and distinct from the fence file.' }
    if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($ResultFile)) -PathType Container)) { throw 'Create the result directory first.' }
}

$script:Root=Join-Path $env:ProgramData 'TechMES\PostgreSqlSetup\Switches'
$null=New-Item -ItemType Directory -Path $script:Root -Force -WhatIf:$false -Confirm:$false
$script:ActiveFencePath=Join-Path $script:Root ($ServiceName+'-active-fence.json')
$run=Join-Path $script:Root ([guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $run -WhatIf:$false -Confirm:$false
$mutex=New-Object Threading.Mutex($false,('Global\TechMES.PostgreSqlSetup.'+$ServiceName))
$locked=$false
$deploymentMutex=$null
$deploymentLocked=$false
$consoleLock=$null
$transcript=$false
$script:Changed=$false
$script:Installed=$false
$script:RebuildRequired=$false
$script:InitialCopy=$null
$exitCode=0

$result=[ordered]@{
    OperationId=$OperationId
    ExpectedSystemIdentifier=$ExpectedSystemIdentifier
    Mode=$Mode
    From=$From
    To=$To
    Computer=$env:COMPUTERNAME
    CheckedAtUtc=''
    Success=$false
    Data=$null
    Error=$null
    Log=(Join-Path $run 'switch.log')
}

try {
    if ($initial) {
        $deploymentMutex=New-Object Threading.Mutex($false,'Global\TechMES.ServerUpdate')
        try { $deploymentLocked=$deploymentMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $deploymentLocked=$true }
        if (!$deploymentLocked) { throw 'A TechMES server update is running on this computer.' }
    }
    try {
        $locked=$mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $locked=$true
    }

    if (!$locked) { throw 'Another local PostgreSQL setup operation is running.' }
    if ($initial) {
        $network=Join-Path $env:ProgramData ('TechMES\PostgreSqlNodes\'+$ServiceName.ToUpperInvariant()+'\Commands\Network')
        $consolePath=Join-Path $network 'console.lock'
        Assert-LocalPath $consolePath
        if (Test-Path -LiteralPath $network -PathType Container) {
            $consoleLock=[IO.File]::Open($consolePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        }
        $null=Get-InitialTarget $ExpectedTargetSystemIdentifier
    }

    Start-Transcript -LiteralPath $result.Log -Force | Out-Null
    $transcript=$true

    # Проверяет именно замену существующего файла до любых изменений PostgreSQL.
    $writeCheck=Join-Path $run 'atomic-write-check.json'
    Write-Json $writeCheck ([pscustomobject]@{ Version=1 })
    Write-Json $writeCheck ([pscustomobject]@{ Version=2 })

    if ($CredentialInput) {
        Read-ProtectedCredentials
    }
    else {
        $script:SqlCredential=Get-Credential -UserName 'postgres' -Message 'PostgreSQL administration account on both nodes'

        if ($null -eq $script:SqlCredential) { throw 'SQL credential entry cancelled.' }

        $script:ReplicationCredential=Get-Credential -UserName 'techmes_repl' -Message 'PostgreSQL replication account'

        if ($null -eq $script:ReplicationCredential) { throw 'Replication credential entry cancelled.' }
    }

    if ($Mode -eq 'Status') {
        $result.Data=[pscustomobject]@{
            FromNode=(Get-Node $From)
            ToNode=(Get-Node $To)
            LocalService=(Get-LocalService)
        }

        $result.Success=$true
    }
    elseif ($Mode -eq 'CheckInitialStandby') {
        $result.Data=Invoke-InitialStandby -CheckOnly
        $result.Success=$true
    }
    elseif ($PSCmdlet.ShouldProcess($script:LocalAddress,($Mode+' PostgreSQL switch '+$From+' -> '+$To))) {
        switch ($Mode) {
            'Fence' { $result.Data=if ($EmergencyFenceId) { Invoke-EmergencyFence } else { Invoke-Fence } }
            'Promote' { $result.Data=Invoke-Promote }
            'Rejoin' { $result.Data=Invoke-Rejoin }
            'Rebuild' { $result.Data=Invoke-Rebuild }
            'InitializeStandby' { $result.Data=Invoke-InitialStandby }
        }

        $result.Success=$true
    }
}
catch {
    $exitCode=1
    $result.Error=$_.Exception.Message

    if ($script:RebuildRequired) {
        $exitCode=2
        $result.Data=[pscustomobject]@{ State='RebuildRequired' }
    }

    if ($initial -and $null -ne $script:InitialCopy) { $result.Data=$script:InitialCopy }

    if ($Mode -in @('Rejoin','Rebuild','InitializeStandby') -and $script:Installed) {
        try {
            Set-Service -Name $ServiceName -StartupType Disabled
            $state=Get-LocalService

            if ($state.ServiceState -eq 'Running') { Stop-Service -Name $ServiceName -ErrorAction Stop }
        }
        catch {
            $result.Error+='; cleanup: '+$_.Exception.Message
        }
    }

    Write-Host ('FAILED: '+$result.Error) -ForegroundColor Red
    if ($initial) { Write-Host 'Keep the original data, work directory and log. Check the Primary slot before retrying.' -ForegroundColor Yellow }
    else { Write-Host 'Do not start the old node as Primary. Retain the exchange file, data directories and logs.' -ForegroundColor Yellow }
}
finally {
    try {
        if ($transcript) { Stop-Transcript | Out-Null }

        $result.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')

        Write-Json (Join-Path $run 'result.json') $result

        if ($ResultFile) { Write-Json $ResultFile $result }
    }
    finally {
        if ($null -ne $consoleLock) { $consoleLock.Dispose() }
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        if ($deploymentLocked) { $deploymentMutex.ReleaseMutex() }
        if ($null -ne $deploymentMutex) { $deploymentMutex.Dispose() }
    }
}

$result | ConvertTo-Json -Depth 12

Write-Host ('Result: '+(Join-Path $run 'result.json')) -ForegroundColor Cyan

exit $exitCode
