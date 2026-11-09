#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param([ValidateSet('Status','Fence','Promote','Rejoin')][string]$Mode='Status', [ValidateSet('192.168.1.132','192.168.1.131')][string]$From='192.168.1.132', [ValidateSet('192.168.1.132','192.168.1.131')][string]$To='192.168.1.131', [string]$FenceFile='', [string]$PgBin='D:\PostgreSQL\18\bin', [string]$DataDirectory='D:\PostgreSQL\18\data', [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName='postgresql-x64-18', [ValidateRange(1,65535)][int]$Port=5432)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
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
        } else {
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
        if ($Change) { $process.WaitForExit() }
        elseif (!$process.WaitForExit(20000)) {
            $process.Kill()
            $process.WaitForExit()
            $null=$stderr.GetAwaiter().GetResult()
            throw ('Read-only check timed out: '+[IO.Path]::GetFileName($File))
        }
        $output=$stdout.GetAwaiter().GetResult().Trim()
        $null=$stderr.GetAwaiter().GetResult()
        try { $errorText=(New-Object Text.UTF8Encoding($false,$true)).GetString($errors.ToArray()).Trim() }
        catch [Text.DecoderFallbackException] { $errorText=[Text.Encoding]::GetEncoding((Get-WinSystemLocale).TextInfo.ANSICodePage).GetString($errors.ToArray()).Trim() }
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
 'replay',pg_last_wal_replay_lsn()::text,
 'paused',CASE WHEN pg_is_in_recovery() THEN pg_is_wal_replay_paused() END,
 'hints',current_setting('wal_log_hints'), 'checksums',current_setting('data_checksums'),
 'fullPages',current_setting('full_page_writes'),
 'syncNames',current_setting('synchronous_standby_names'),
 'tablespaces',(SELECT count(*) FROM pg_tablespace WHERE spcname NOT IN ('pg_default','pg_global')),
 'superuser',(SELECT rolsuper FROM pg_roles WHERE rolname=current_user),
 'configErrors',(SELECT count(*) FROM pg_file_settings WHERE error IS NOT NULL)+(SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL),
 'configs',(SELECT jsonb_agg(DISTINCT path) FROM (
    SELECT sourcefile AS path FROM pg_file_settings
    UNION SELECT file_name FROM pg_hba_file_rules
    UNION SELECT current_setting('config_file') UNION SELECT current_setting('hba_file') UNION SELECT current_setting('ident_file')
    UNION SELECT current_setting(key) FROM unnest(ARRAY['ssl_cert_file','ssl_key_file','ssl_ca_file','ssl_crl_file']) AS key WHERE current_setting('ssl')='on' AND current_setting(key)<>''
 ) AS files WHERE path IS NOT NULL),
 'receiver',COALESCE((SELECT jsonb_agg(jsonb_build_object('host',sender_host,'port',sender_port,'slot',slot_name,'status',status)) FROM pg_stat_wal_receiver),'[]'::jsonb),
 'replicas',COALESCE((SELECT jsonb_agg(jsonb_build_object('address',host(client_addr),'state',state)) FROM pg_stat_replication),'[]'::jsonb)
);
COMMIT;
'@
    return (Invoke-Sql $Address $sql) | ConvertFrom-Json
}

# Проверяет действующий вход по физическому протоколу репликации.
function Assert-ReplicationLogin([string]$Address, [string]$SystemIdentifier) {
    $raw=Invoke-Tool (Join-Path $PgBin 'psql.exe') @('-X','-q','-w','-A','-t','-d','replication=1','-c','IDENTIFY_SYSTEM;') '' $script:ReplicationCredential $Address
    $fields=$raw -split '\|'
    if ($fields.Count -ne 4 -or $fields[0] -cne $SystemIdentifier) { throw 'Replication identity does not match the selected cluster.' }
}

# Использует единственный штатный инструмент проверки принадлежности процесса службе.
function Get-LocalService {
    $raw=Invoke-Tool $script:WindowsPowerShell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script:ServiceTool,'-Mode','Status','-AsJson','-ServiceName',$ServiceName,'-PgBin',$PgBin,'-DataDirectory',$DataDirectory,'-Port',[string]$Port)
    return $raw | ConvertFrom-Json
}

# Требует подтверждённое владение работающим PostgreSQL либо его полную остановку.
function Assert-ServiceState([bool]$Running) {
    $state=Get-LocalService
    if ($Running) {
        if (!$state.ManagedByService -or $state.ServiceState -ne 'Running') { throw 'Local PostgreSQL is not managed by its running Windows service.' }
    } elseif ($state.ServiceState -ne 'Stopped' -or $state.ListenerProcessId -ne 0 -or $state.PostmasterProcessId -ne 0) {
        throw 'Local PostgreSQL shutdown is not confirmed.'
    }
    return $state
}

# Читает контрольный файл остановленного PostgreSQL в фиксированной локали C.
function Get-Control([string]$Directory) {
    $raw=Invoke-Tool (Join-Path $PgBin 'pg_controldata.exe') @('-D',$Directory)
    $values=@{}
    foreach ($line in $raw -split '\r?\n') {
        if ($line -match '^([^:]+):\s*(.*?)\s*$') { $values[$matches[1].Trim()]=$matches[2] }
    }
    foreach ($key in @('Database system identifier','Database cluster state','Latest checkpoint location',"Latest checkpoint's TimeLineID")) {
        if (!$values.ContainsKey($key)) { throw ('Missing pg_controldata field: '+$key) }
    }
    if ($values['Database cluster state'] -ne 'shut down') { throw 'The old Primary must be shut down cleanly before promotion or rewind.' }
    return [pscustomobject]@{ Sid=$values['Database system identifier']; Lsn=$values['Latest checkpoint location']; Timeline=[int]$values["Latest checkpoint's TimeLineID"] }
}

# Сохраняет локальный журнал и файлы обмена атомарно.
function Write-Json([string]$Path, $Value) {
    $temporary=$Path+'.tmp'
    [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($true)))
    Move-Item -LiteralPath $temporary -Destination $Path -Force -WhatIf:$false -Confirm:$false
}

# Проверяет, что подтверждение остановки относится к заданной операции и профилю.
function Read-Fence {
    $proof=Get-Content -LiteralPath $FenceFile -Raw | ConvertFrom-Json
    $identifier=[guid]::Parse($proof.Id).ToString('N')
    if ($identifier -cne $proof.Id -or $proof.Format -ne 1 -or $proof.State -ne 'Fenced' -or $proof.From -ne $From -or $proof.To -ne $To -or $proof.ServiceName -ne $ServiceName -or $proof.Port -ne $Port -or (Normalize-Path $proof.DataDirectory) -ine $DataDirectory -or (Normalize-Path $proof.PgBin) -ine $PgBin -or $proof.StartMode -notin @('Auto','Manual') -or $proof.Lsn -notmatch '^[0-9A-F]+/[0-9A-F]+$' -or $proof.Sid -notmatch '^\d+$' -or $proof.Timeline -lt 1) { throw 'Fence file does not match this operation.' }
    if ($Mode -eq 'Promote') {
        $age=[DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($proof.CheckedAtUtc)
        if ($age.TotalMinutes -gt 30 -or $age.TotalMinutes -lt -1) { throw 'Fence confirmation must be less than 30 minutes old.' }
    }
    return $proof
}

# Проверяет базовые условия и локальные файлы до остановки Primary.
function Assert-Pair($Primary, $Standby) {
    if ($Primary.recovery -or $Primary.readOnly -or !$Standby.recovery -or $Standby.paused -or $Primary.sid -cne $Standby.sid -or $Primary.timeline -ne $Standby.timeline) { throw 'Expected one writable Primary and its unpaused Standby in the same cluster and timeline.' }
    foreach ($node in @($Primary,$Standby)) {
        if ($node.version -lt 180000 -or $node.version -ge 190000 -or $node.version -ne $Primary.version -or !$node.superuser -or $node.configErrors -ne 0 -or $node.tablespaces -ne 0 -or $node.syncNames -ne '' -or (Normalize-Path $node.data) -ine $DataDirectory -or $node.fullPages -ne 'on' -or ($node.hints -ne 'on' -and $node.checksums -ne 'on')) { throw 'Unsupported PostgreSQL settings, permissions or storage layout for this first planned test.' }
    }
    if (@($Standby.receiver | Where-Object { $_.host -eq $From -and $_.port -eq $Port -and $_.status -eq 'streaming' }).Count -ne 1 -or @($Primary.replicas | Where-Object { $_.address -eq $To -and $_.state -eq 'streaming' }).Count -ne 1) { throw 'The selected replication stream is not healthy.' }
    $null=Assert-ServiceState $true
    Assert-ReplicationLogin $To $Primary.sid
    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside PGDATA are not supported.' }
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
function Ensure-Slot([string]$Address) {
    $sql="SELECT jsonb_build_object('type',slot_type,'active',active,'status',wal_status) FROM pg_replication_slots WHERE slot_name='$script:Slot';"
    $raw=Invoke-Sql $Address $sql
    if (!$raw) {
        $null=Invoke-Sql $Address ("SELECT pg_create_physical_replication_slot('"+$script:Slot+"',true);") -Change
        $raw=Invoke-Sql $Address $sql
    }
    $slot=$raw | ConvertFrom-Json
    if ($slot.type -ne 'physical' -or $slot.active -or $slot.status -in @('lost','unreserved') -or $null -eq $slot.status) { throw 'The rejoin slot is active elsewhere or does not retain the required WAL.' }
}

# Экранирует значение строки подключения libpq.
function Quote-Libpq([string]$Value) { return "'"+$Value.Replace('\','\\').Replace("'","\'")+"'" }

# Временно разрешает текущему пользователю rewind копии; перед установкой возвращает исходный DACL.
function Set-WorkingCopyAccess([string]$Stage, [switch]$ForRewind) {
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetSecurityDescriptorSddlForm((Get-Acl -LiteralPath $DataDirectory).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access),[Security.AccessControl.AccessControlSections]::Access)
    if ($ForRewind) {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        try { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity.User,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
        finally { $identity.Dispose() }
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
    $account=if ($service.StartName -eq 'LocalSystem') { New-Object Security.Principal.SecurityIdentifier('S-1-5-18') } else { (New-Object Security.Principal.NTAccount($service.StartName)).Translate([Security.Principal.SecurityIdentifier]) }
    $acl=New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-32-544','S-1-5-18')) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','Allow'))) }
    if ($account.Value -notin @('S-1-5-18','S-1-5-32-544')) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account,'Read','Allow'))) }
    Set-Acl -LiteralPath $passPath -AclObject $acl
    [IO.File]::WriteAllText($passPath,$line,(New-Object Text.UTF8Encoding($false)))
    $connection='host='+(Quote-Libpq $To)+' port='+$Port+' user='+(Quote-Libpq $script:ReplicationCredential.UserName)+' passfile='+(Quote-Libpq $finalPassPath)+' application_name='+(Quote-Libpq $script:Slot)+' connect_timeout=5'
    $configuration="`r`n# TechMES planned switch "+$Proof.Id+"`r`nprimary_conninfo = '"+$connection.Replace('\','\\').Replace("'","''")+"'`r`nprimary_slot_name = '"+$script:Slot+"'`r`nrecovery_target_timeline = 'latest'`r`n"
    foreach ($setting in @('recovery_target','recovery_target_name','recovery_target_time','recovery_target_xid','recovery_target_lsn','restore_command')) { $configuration+=$setting+" = ''`r`n" }
    $configuration+="recovery_target_action = 'pause'`r`n"
    $stream=[IO.File]::Open((Join-Path $Stage 'postgresql.auto.conf'),[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($configuration); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $Stage 'standby.signal'),[byte[]]@())
    if (Test-Path -LiteralPath (Join-Path $Stage 'recovery.signal')) { throw 'Unexpected recovery.signal in the prepared directory.' }
}

# Проверяет устойчивую работу возвращённого узла в режиме реплики.
function Wait-Standby($Proof) {
    $deadline=[DateTime]::UtcNow.AddSeconds(180)
    $good=0
    do {
        $node=$null
        try {
            $null=Assert-ServiceState $true
            $node=Get-Node '127.0.0.1'
        }
        catch { $good=0; Write-Host ('Waiting for Standby startup: '+$_.Exception.Message) }
        if ($null -eq $node) { Start-Sleep -Seconds 10; continue }
        if (!$node.recovery -or $node.sid -cne $Proof.Sid) { throw 'The returning node is not a Standby of the expected cluster.' }
        if (@($node.receiver | Where-Object { $_.host -eq $To -and $_.port -eq $Port -and $_.slot -eq $script:Slot -and $_.status -eq 'streaming' }).Count -eq 1 -and !$node.paused) { $good++ } else { $good=0 }
        if ($good -ge 7) { return $node }
        Start-Sleep -Seconds 10
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The returning Standby did not confirm stable streaming replication.'
}

# Останавливает прежний Primary, запрещает запуск службы и оставляет постоянный standby.signal.
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
    $proof=[pscustomobject]@{ Format=1; Id=[guid]::NewGuid().ToString('N'); State='Fencing'; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); From=$From; To=$To; ServiceName=$ServiceName; PgBin=$PgBin; DataDirectory=$DataDirectory; Port=$Port; StartMode=$service.StartMode; Sid=$primary.sid; Timeline=$primary.timeline; Lsn=''; Configs=$configs }
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
    return [pscustomobject]@{ State='Fenced'; ExchangeFile=$FenceFile; FinalCheckpoint=$proof.Lsn; Next=('Copy the exchange file to '+$To+' and run Promote.') }
}

# Повышает только догнавшую реплику после подтверждённой остановки старого Primary.
function Invoke-Promote {
    if ($script:LocalAddress -ne $To) { throw 'Promote must run on the selected new Primary.' }
    $proof=Read-Fence
    $null=Assert-ServiceState $true
    $node=Get-Node '127.0.0.1'
    if ($node.sid -cne $proof.Sid -or $node.readOnly -or $node.version -lt 180000 -or $node.version -ge 190000) { throw 'The new Primary identity or settings do not match.' }
    $journalPath=Join-Path $script:Root ('promotion-'+$proof.Id+'.json')
    $oldResponded=$false
    try { $null=Get-Node $From; $oldResponded=$true } catch { Write-Host 'Old Primary SQL endpoint is not responding; the exchange file and persistent local fencing are required.' }
    if ($oldResponded) { throw 'The old PostgreSQL endpoint is still responding. Inspect its state before promotion.' }
    if ($node.recovery) {
        if (Test-Path -LiteralPath $journalPath) { throw 'A promotion was already requested. Inspect its journal before issuing another request.' }
        Wait-Replay '127.0.0.1' $proof.Lsn $proof.Sid $proof.Timeline
        $null=Assert-ServiceState $true
        Write-Json $journalPath ([pscustomobject]@{ Id=$proof.Id; State='PromotionRequested'; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
        $script:Changed=$true
        $null=Invoke-Sql '127.0.0.1' 'SELECT pg_promote(true,60);' -Change
        $node=Get-Node '127.0.0.1'
    } elseif (!(Test-Path -LiteralPath $journalPath)) {
        throw 'This node was promoted outside this operation; automatic continuation is refused.'
    }
    if ($node.recovery -or $node.readOnly -or $node.sid -cne $proof.Sid) { throw 'Promotion is not confirmed. The old node must remain fenced.' }
    $script:Changed=$true
    Ensure-Slot '127.0.0.1'
    $null=Invoke-Sql '127.0.0.1' 'CHECKPOINT;' -Change
    $node=Get-Node '127.0.0.1'
    if ($node.recovery -or $node.readOnly -or $node.sid -cne $proof.Sid -or $node.timeline -le $proof.Timeline) { throw 'The new Primary timeline is not confirmed after checkpoint. The old node must remain fenced.' }
    Write-Json $journalPath ([pscustomobject]@{ Id=$proof.Id; State='Promoted'; Timeline=$node.timeline; Slot=$script:Slot; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    return [pscustomobject]@{ State='Promoted'; Primary=$To; Timeline=$node.timeline; Slot=$script:Slot; Next=('Run Rejoin on '+$From+' using the same exchange file.') }
}

# Возвращает старый узел через pg_rewind рабочей копии; исходный каталог сохраняется целиком.
function Invoke-Rejoin {
    if ($script:LocalAddress -ne $From) { throw 'Rejoin must run on the old Primary.' }
    $proof=Read-Fence
    $local=Get-Content -LiteralPath $script:ActiveFencePath -Raw | ConvertFrom-Json
    if ($local.Id -cne $proof.Id -or $local.State -ne 'Fenced') { throw 'The local fence marker does not match this operation.' }
    $null=Assert-ServiceState $false
    if ((Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode -ne 'Disabled' -or !(Test-Path -LiteralPath (Join-Path $DataDirectory 'standby.signal'))) { throw 'The old node must remain stopped, disabled and marked as Standby.' }
    $control=Get-Control $DataDirectory
    if ($control.Sid -cne $proof.Sid -or $control.Lsn -cne $proof.Lsn -or $control.Timeline -ne $proof.Timeline) { throw 'The old data directory changed after fencing.' }
    $primary=Get-Node $To
    if ($primary.recovery -or $primary.readOnly -or $primary.sid -cne $proof.Sid -or $primary.timeline -le $proof.Timeline) { throw 'The selected new Primary is not confirmed.' }
    Assert-ReplicationLogin $To $proof.Sid
    Ensure-Slot $To
    if (Get-ChildItem -LiteralPath $DataDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Reparse points inside PGDATA are not supported.' }
    $stage=$DataDirectory+'.techmes-stage-'+$proof.Id
    $backup=$DataDirectory+'.techmes-before-'+$proof.Id
    if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) { throw 'A working copy or backup already exists. Inspect the previous run before retrying.' }
    $script:Changed=$true
    $null=New-Item -ItemType Directory -Path $stage
    Set-WorkingCopyAccess $stage -ForRewind
    $null=Invoke-Tool (Join-Path $env:SystemRoot 'System32\robocopy.exe') @($DataDirectory,$stage,'/E','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/XJ','/NFL','/NDL') -Change -SuccessCodeMaximum 7
    $stageControl=Get-Control $stage
    if ($stageControl.Sid -cne $proof.Sid -or $stageControl.Lsn -cne $proof.Lsn) { throw 'Working copy control data does not match the original.' }
    $source='host='+(Quote-Libpq $To)+' port='+$Port+' dbname=postgres user='+(Quote-Libpq $script:SqlCredential.UserName)+' connect_timeout=5'
    $rewindArguments=@(('--target-pgdata='+$stage),('--source-server='+$source),'--no-ensure-shutdown','--progress')
    $null=Invoke-Tool (Join-Path $PgBin 'pg_rewind.exe') ($rewindArguments+@('--dry-run')) '' $script:SqlCredential $To -Change
    $null=Invoke-Tool (Join-Path $PgBin 'pg_rewind.exe') $rewindArguments '' $script:SqlCredential $To -Change
    foreach ($relative in $proof.Configs) {
        $relative=Get-ConfigRelativePath ([string]$relative)
        $original=Join-Path $DataDirectory $relative
        if (Test-Path -LiteralPath $original -PathType Leaf) {
            $target=Join-Path $stage $relative
            $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            [IO.File]::Copy($original,$target,$true)
        }
    }
    Write-StandbyConfiguration $stage $proof
    Set-WorkingCopyAccess $stage
    Ensure-Slot $To
    $null=Assert-ServiceState $false
    if ((Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode -ne 'Disabled') { throw 'Service startup type changed before data installation.' }
    Write-Json (Join-Path $script:Root ('rejoin-'+$proof.Id+'.json')) ([pscustomobject]@{ State='Installing'; Stage=$stage; Backup=$backup; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    [IO.Directory]::Move($DataDirectory,$backup)
    [IO.Directory]::Move($stage,$DataDirectory)
    $script:Installed=$true
    $startup=if ($proof.StartMode -eq 'Auto') { 'Automatic' } else { 'Manual' }
    Set-Service -Name $ServiceName -StartupType $startup
    Start-Service -Name $ServiceName -ErrorAction Stop
    (Get-Service -Name $ServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(300))
    $node=Wait-Standby $proof
    Write-Json (Join-Path $script:Root ('rejoin-'+$proof.Id+'.json')) ([pscustomobject]@{ State='Rejoined'; Backup=$backup; CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') })
    Remove-Item -LiteralPath $script:ActiveFencePath
    return [pscustomobject]@{ State='Rejoined'; Primary=$To; Standby=$From; Backup=$backup; Receiver=$node.receiver; Next='Verify application database routing and retain the backup.' }
}

# Требует локальный запуск с административными правами Windows.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run on a Windows PostgreSQL server.' }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
} finally { $identity.Dispose() }
if ($From -eq $To) { throw 'From and To must be different database nodes.' }
$PgBin=Normalize-Path $PgBin
$DataDirectory=Normalize-Path $DataDirectory
Assert-LocalPath $PgBin
Assert-LocalPath $DataDirectory
$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
$local=@($From,$To | Where-Object { $_ -in $addresses })
if ($local.Count -ne 1) { throw 'Run locally on exactly one selected database node.' }
$script:LocalAddress=$local[0]
$script:Slot=if ($From -eq '192.168.1.132') { 'techmes_primary' } else { 'techmes_secondary' }
$script:WindowsPowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:ServiceTool=Join-Path $PSScriptRoot 'Set-PostgreSQL18ServiceControl.ps1'
foreach ($file in @('psql.exe','pg_rewind.exe','pg_controldata.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $PgBin $file) -PathType Leaf)) { throw ('Required tool missing: '+$file) }
}
if (!(Test-Path -LiteralPath $script:ServiceTool -PathType Leaf)) { throw 'Place Set-PostgreSQL18ServiceControl.ps1 beside this script.' }
if ($Mode -ne 'Status') {
    if (!$FenceFile) { throw 'Specify an exchange file using -FenceFile.' }
    $FenceFile=[IO.Path]::GetFullPath($FenceFile)
    Assert-LocalPath $FenceFile
    if ($FenceFile.StartsWith($DataDirectory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The exchange file must be outside PGDATA.' }
    if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($FenceFile)) -PathType Container)) { throw 'Create the exchange directory before running this script.' }
}
$script:Root=Join-Path $env:ProgramData 'TechMES\PostgreSqlSetup\Switches'
$null=New-Item -ItemType Directory -Path $script:Root -Force -WhatIf:$false -Confirm:$false
$script:ActiveFencePath=Join-Path $script:Root ($ServiceName+'-active-fence.json')
$run=Join-Path $script:Root ([guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $run -WhatIf:$false -Confirm:$false
$mutex=New-Object Threading.Mutex($false,('Global\TechMES.PostgreSqlSetup.'+$ServiceName))
$locked=$false
$transcript=$false
$script:Changed=$false
$script:Installed=$false
$exitCode=0
$result=[ordered]@{ Mode=$Mode; From=$From; To=$To; Computer=$env:COMPUTERNAME; CheckedAtUtc=''; Success=$false; Data=$null; Error=$null; Log=(Join-Path $run 'switch.log') }
try {
    try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (!$locked) { throw 'Another local PostgreSQL setup operation is running.' }
    Start-Transcript -LiteralPath $result.Log -Force | Out-Null
    $transcript=$true
    $script:SqlCredential=Get-Credential -UserName 'postgres' -Message 'PostgreSQL administration account on both nodes'
    if ($null -eq $script:SqlCredential) { throw 'SQL credential entry cancelled.' }
    $script:ReplicationCredential=Get-Credential -UserName 'techmes_repl' -Message 'PostgreSQL replication account'
    if ($null -eq $script:ReplicationCredential) { throw 'Replication credential entry cancelled.' }
    if ($Mode -eq 'Status') {
        $result.Data=[pscustomobject]@{ FromNode=(Get-Node $From); ToNode=(Get-Node $To); LocalService=(Get-LocalService) }
        $result.Success=$true
    } elseif ($PSCmdlet.ShouldProcess($script:LocalAddress,($Mode+' PostgreSQL planned switch '+$From+' -> '+$To))) {
        switch ($Mode) {
            'Fence' { $result.Data=Invoke-Fence }
            'Promote' { $result.Data=Invoke-Promote }
            'Rejoin' { $result.Data=Invoke-Rejoin }
        }
        $result.Success=$true
    }
}
catch {
    $exitCode=1
    $result.Error=$_.Exception.Message
    if ($Mode -eq 'Rejoin' -and $script:Installed) {
        try {
            Set-Service -Name $ServiceName -StartupType Disabled
            $state=Get-LocalService
            if ($state.ServiceState -eq 'Running') { Stop-Service -Name $ServiceName -ErrorAction Stop }
        }
        catch { $result.Error+='; cleanup: '+$_.Exception.Message }
    }
    Write-Host ('FAILED: '+$result.Error) -ForegroundColor Red
    Write-Host 'Do not start the old node as Primary. Retain the exchange file, data directories and logs.' -ForegroundColor Yellow
}
finally {
    if ($transcript) { Stop-Transcript | Out-Null }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    $result.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Write-Json (Join-Path $run 'result.json') $result
}
$result | ConvertTo-Json -Depth 12
Write-Host ('Result: '+(Join-Path $run 'result.json')) -ForegroundColor Cyan
exit $exitCode