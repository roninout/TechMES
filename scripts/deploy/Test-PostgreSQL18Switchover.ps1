#requires -Version 5.1
[CmdletBinding()]
param([string]$PgBin='D:\PostgreSQL\18\bin', [string]$ServiceName='postgresql-x64-18', [ValidateRange(1,65535)][int]$Port=5432, [string]$OutputDirectory='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'

# Разделяет административное SQL-подключение и вход пользователя репликации.
function Get-PgCheckCredentials {
    $sql=Get-Credential -UserName 'postgres' -Message 'PostgreSQL SQL diagnostics and pg_rewind permissions'

    if ($null -eq $sql) {
        throw 'SQL credential entry cancelled.'
    }

    $replicationCredential=Get-Credential -UserName 'techmes_repl' -Message 'PostgreSQL physical replication account'

    if ($null -eq $replicationCredential) {
        throw 'Replication credential entry cancelled.'
    }

    return [pscustomobject]@{
        Sql=$sql
        Replication=$replicationCredential
    }
}

# Читает UTF-8 строго, затем использует системную ANSI-кодировку Windows.
function ConvertFrom-PgDiagnosticBytes([byte[]]$Bytes) {
    try {
        return (New-Object Text.UTF8Encoding($false,$true)).GetString($Bytes).Trim()
    }
    catch [Text.DecoderFallbackException] {
        $locale=Get-WinSystemLocale
        return [Text.Encoding]::GetEncoding($locale.TextInfo.ANSICodePage).GetString($Bytes).Trim()
    }
}

# Сохраняет сведения о процессе и события SCM для выяснения причин остановки службы.
function Get-PgServiceDiagnostics($State) {
    $service=Get-CimInstance Win32_Service -Filter ("Name='"+$ServiceName.Replace("'","''")+"'")

    if ($null -eq $service) {
        throw 'PostgreSQL Windows service was not found during diagnostics.'
    }

    $server=$null
    $parent=$null

    if ($State.PostmasterProcessId -gt 0) {
        $server=Get-CimInstance Win32_Process -Filter ('ProcessId='+$State.PostmasterProcessId)

        if ($null -ne $server -and $server.ParentProcessId -gt 0) {
            $candidate=Get-CimInstance Win32_Process -Filter ('ProcessId='+$server.ParentProcessId)

            if ($null -ne $candidate -and $null -ne $candidate.CreationDate -and $null -ne $server.CreationDate -and $candidate.CreationDate -le $server.CreationDate) {
                $parent=$candidate
            }
        }
    }

    $events=@()
    $eventsReadError=$null

    try {
        $filter=@{
            LogName='System'
            ProviderName='Service Control Manager'
            StartTime=(Get-Date).AddDays(-7)
            Id=@(7000,7001,7009,7011,7023,7024,7031,7034,7036,7038)
        }

        $events=@(Get-WinEvent -FilterHashtable $filter -MaxEvents 2000 -ErrorAction Stop | Where-Object { $_.Properties.Count -gt 0 -and $_.Properties[0].Value -in @($service.Name,$service.DisplayName) } | Select-Object -First 12 -Property TimeCreated,Id,Message)
    }
    catch {
        $eventsReadError=$_.Exception.Message
    }

    return [pscustomobject]@{
        CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        StartMode=$service.StartMode
        Server=($server | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath)
        Parent=($parent | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath)
        Events=$events
        EventsReadError=$eventsReadError
    }
}

# Выполняет диагностический SQL либо IDENTIFY_SYSTEM.
# Пароль передаётся дочернему процессу без записи в файл.
function Invoke-PgCheck([string]$Address, [PSCredential]$Credential, [string]$Sql, [switch]$PhysicalReplication) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=Join-Path $PgBin 'psql.exe'
    $start.Arguments='-X -q -w -A -t -v ON_ERROR_STOP=1 -f -'

    if ($PhysicalReplication) {
        $start.Arguments+=' -d replication=1'
    }

    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)

    foreach ($name in @('PGHOSTADDR','PGSERVICE','PGSERVICEFILE','PGPASSFILE','PGOPTIONS','PGTARGETSESSIONATTRS')) {
        $start.EnvironmentVariables.Remove($name)
    }

    $start.EnvironmentVariables['PGHOST']=$Address
    $start.EnvironmentVariables['PGPORT']=[string]$Port
    $start.EnvironmentVariables['PGUSER']=$Credential.UserName
    $start.EnvironmentVariables['PGPASSWORD']=$Credential.GetNetworkCredential().Password
    $start.EnvironmentVariables['PGDATABASE']='postgres'
    $start.EnvironmentVariables['PGCONNECT_TIMEOUT']='5'
    $start.EnvironmentVariables['PGCLIENTENCODING']='UTF8'
    $start.EnvironmentVariables['PGAPPNAME']='TechMES.SwitchoverPreflight'
    $start.EnvironmentVariables['PGOPTIONS']='-c statement_timeout=10000 -c lock_timeout=3000'

    $process=New-Object Diagnostics.Process
    $process.StartInfo=$start
    $errorBytes=New-Object IO.MemoryStream

    try {
        $null=$process.Start()

        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.BaseStream.CopyToAsync($errorBytes)

        $process.StandardInput.WriteLine($Sql)
        $process.StandardInput.Close()

        if (!$process.WaitForExit(20000)) {
            $process.Kill()
            $process.WaitForExit()
            $null=$stderr.GetAwaiter().GetResult()

            throw ('Read-only PostgreSQL check timed out: '+$Address)
        }

        $output=$stdout.GetAwaiter().GetResult().Trim()
        $null=$stderr.GetAwaiter().GetResult()
        $errorText=ConvertFrom-PgDiagnosticBytes ($errorBytes.ToArray())

        if ($process.ExitCode -ne 0) {
            throw ('PostgreSQL check failed on '+$Address+': '+$errorText)
        }

        return $output
    }
    finally {
        $start.EnvironmentVariables.Remove('PGPASSWORD')
        $errorBytes.Dispose()
        $process.Dispose()
    }
}

# Собирает роли, WAL, настройки и права pg_rewind без строк подключения и паролей.
function Get-PgSnapshot([string]$Address, [PSCredential]$Credential) {
    $sql=@'
BEGIN READ ONLY;

SELECT jsonb_build_object(
    'checkedAt', clock_timestamp(),
    'address', host(inet_server_addr()),
    'port', inet_server_port(),
    'user', current_user,
    'systemIdentifier', (SELECT system_identifier::text FROM pg_control_system()),
    'timeline', (SELECT timeline_id FROM pg_control_checkpoint()),
    'inRecovery', pg_is_in_recovery(),

    'rewindPrivileges', (
        SELECT bool_and(has_function_privilege(current_user, signature, 'EXECUTE'))
        FROM (
            VALUES
                ('pg_catalog.pg_ls_dir(text,boolean,boolean)'),
                ('pg_catalog.pg_stat_file(text,boolean)'),
                ('pg_catalog.pg_read_binary_file(text)'),
                ('pg_catalog.pg_read_binary_file(text,bigint,bigint,boolean)')
        ) AS permissions(signature)
    ),

    'settings', (
        SELECT jsonb_object_agg(name, jsonb_build_object('value', setting, 'unit', unit, 'pendingRestart', pending_restart))
        FROM pg_settings
        WHERE name IN (
            'server_version_num',
            'data_directory',
            'config_file',
            'hba_file',
            'ident_file',
            'port',
            'wal_level',
            'hot_standby',
            'wal_log_hints',
            'full_page_writes',
            'data_checksums',
            'wal_keep_size',
            'max_slot_wal_keep_size',
            'max_wal_senders',
            'max_replication_slots',
            'synchronous_commit',
            'synchronous_standby_names',
            'default_transaction_read_only',
            'recovery_target_timeline',
            'archive_mode'
        )
    ),

    'wal', jsonb_build_object(
        'flushLsn', CASE WHEN NOT pg_is_in_recovery() THEN pg_current_wal_flush_lsn()::text END,
        'receiveLsn', pg_last_wal_receive_lsn()::text,
        'replayLsn', pg_last_wal_replay_lsn()::text,
        'replayPaused', CASE WHEN pg_is_in_recovery() THEN pg_is_wal_replay_paused() END
    ),

    'receiver', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('status', status, 'senderHost', sender_host, 'senderPort', sender_port, 'slotName', slot_name))
        FROM pg_stat_wal_receiver
    ), '[]'::jsonb),

    'replicas', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('application', application_name, 'user', usename, 'address', host(client_addr), 'state', state, 'syncState', sync_state, 'sentLsn', sent_lsn::text, 'flushLsn', flush_lsn::text, 'replayLsn', replay_lsn::text))
        FROM pg_stat_replication
    ), '[]'::jsonb),

    'slots', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('name', slot_name, 'type', slot_type, 'active', active, 'restartLsn', restart_lsn::text, 'walStatus', wal_status, 'invalidationReason', invalidation_reason))
        FROM pg_replication_slots
    ), '[]'::jsonb),

    'tablespaces', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('name', spcname, 'path', pg_tablespace_location(oid)))
        FROM pg_tablespace
        WHERE spcname NOT IN ('pg_default','pg_global')
    ), '[]'::jsonb),

    'configErrors', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('file', sourcefile, 'line', sourceline, 'name', name))
        FROM pg_file_settings
        WHERE error IS NOT NULL
    ), '[]'::jsonb),

    'hba', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('file', file_name, 'line', line_number, 'type', type, 'database', database, 'users', user_name, 'address', address, 'mask', netmask, 'method', auth_method, 'invalid', error IS NOT NULL))
        FROM pg_hba_file_rules
    ), '[]'::jsonb)
);

COMMIT;
'@

    return (Invoke-PgCheck $Address $Credential $sql) | ConvertFrom-Json
}

# Проверяет реальный вход по физическому протоколу репликации на соседний сервер.
function Get-PgReplicationIdentity([string]$Address, [PSCredential]$Credential) {
    $line=Invoke-PgCheck $Address $Credential 'IDENTIFY_SYSTEM;' -PhysicalReplication
    $fields=$line -split '\|'

    if ($fields.Count -ne 4 -or $fields[0] -notmatch '^\d+$' -or $fields[1] -notmatch '^\d+$' -or $fields[2] -notmatch '^[0-9A-F]+/[0-9A-F]+$') {
        throw 'Unexpected IDENTIFY_SYSTEM response.'
    }

    return [pscustomobject]@{
        SystemIdentifier=$fields[0]
        Timeline=[long]$fields[1]
        WalLsn=$fields[2]
    }
}

# Сравнивает свежие снимки.
# Успешная диагностика не запускает остановку или повышение сервера.
function Get-PgPreflightIssues($Report) {
    $issues=New-Object 'Collections.Generic.List[string]'

    if ($null -eq $Report.Local -or $null -eq $Report.Peer) {
        $issues.Add('Both SQL snapshots are required.')
        return $issues.ToArray()
    }

    $expectedLocalRecovery=$Report.LocalAddress -eq '192.168.1.131'

    if ($Report.Local.inRecovery -ne $expectedLocalRecovery -or $Report.Peer.inRecovery -eq $expectedLocalRecovery) {
        $issues.Add('Expected .132 Primary and .131 Standby; inspect actual roles first.')
    }

    if ($Report.Local.systemIdentifier -ne $Report.Peer.systemIdentifier) {
        $issues.Add('The nodes belong to different PostgreSQL clusters.')
    }

    if ($Report.Local.settings.server_version_num.value -ne $Report.Peer.settings.server_version_num.value) {
        $issues.Add('Use the same PostgreSQL 18 release on both nodes before the planned switch.')
    }

    if ($Report.Peer.address -ne $Report.PeerAddress -or $Report.Local.port -ne $Report.Port -or $Report.Peer.port -ne $Report.Port) {
        $issues.Add('SQL endpoints do not match the selected nodes and port.')
    }

    foreach ($node in @($Report.Local,$Report.Peer)) {
        if ([int]$node.settings.server_version_num.value -lt 180000 -or [int]$node.settings.server_version_num.value -ge 190000) {
            $issues.Add('PostgreSQL 18 is required on both nodes.')
        }

        if ($node.settings.full_page_writes.value -ne 'on' -or ($node.settings.data_checksums.value -ne 'on' -and $node.settings.wal_log_hints.value -ne 'on')) {
            $issues.Add('pg_rewind prerequisites are not satisfied on '+$node.address)
        }

        if (!$node.rewindPrivileges) {
            $issues.Add('The selected PostgreSQL account lacks pg_rewind privileges on '+$node.address)
        }

        if ($node.settings.wal_level.value -notin @('replica','logical') -or [int]$node.settings.max_wal_senders.value -lt 1 -or [int]$node.settings.max_replication_slots.value -lt 1 -or $node.settings.hot_standby.value -ne 'on') {
            $issues.Add('Check replication settings on '+$node.address)
        }

        if (@($node.configErrors).Count -gt 0 -or @($node.hba | Where-Object { $_.invalid }).Count -gt 0) {
            $issues.Add('Configuration errors exist on '+$node.address)
        }

        if (@($node.settings.PSObject.Properties | Where-Object { $_.Value.pendingRestart }).Count -gt 0) {
            $issues.Add('Settings require a PostgreSQL restart on '+$node.address)
        }
    }

    $standby=if ($expectedLocalRecovery) { $Report.Local } else { $Report.Peer }
    $primary=if ($expectedLocalRecovery) { $Report.Peer } else { $Report.Local }

    if ($primary.settings.default_transaction_read_only.value -ne 'off') {
        $issues.Add('The current Primary defaults to read-only transactions.')
    }

    if (@($standby.receiver | Where-Object { $_.status -eq 'streaming' -and $_.senderHost -eq '192.168.1.132' -and $_.senderPort -eq $Report.Port }).Count -ne 1 -or $standby.wal.replayPaused -ne $false) {
        $issues.Add('Standby is not replaying the expected Primary stream.')
    }

    if ($null -eq $Report.PeerReplication -or $Report.PeerReplication.SystemIdentifier -ne $Report.Peer.systemIdentifier) {
        $issues.Add('Physical replication login to the peer is not confirmed.')
    }

    if ($null -eq $Report.Service -or !$Report.Service.ManagedByService -or $Report.Service.ServiceState -ne 'Running') {
        $issues.Add('Local PostgreSQL Windows service ownership is not confirmed.')
    }

    return $issues.ToArray()
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this script on a Windows database server.'
}

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()

try {
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run PowerShell as Administrator.'
    }
}
finally {
    $identity.Dispose()
}

$addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
$localAddresses=@('192.168.1.132','192.168.1.131' | Where-Object { $_ -in $addresses })

if ($localAddresses.Count -ne 1) {
    throw 'Run this script locally on exactly one of the database nodes: .132 or .131.'
}

if (!(Test-Path -LiteralPath (Join-Path $PgBin 'psql.exe') -PathType Leaf)) {
    throw 'psql.exe was not found in PgBin.'
}

$tool=Join-Path $PSScriptRoot 'Set-PostgreSQL18ServiceControl.ps1'

if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
    throw 'Place the canonical Set-PostgreSQL18ServiceControl.ps1 beside this script.'
}

if (!$OutputDirectory) {
    $OutputDirectory=Join-Path $env:ProgramData 'TechMES\PostgreSqlSetup\SwitchoverChecks'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$localAddress=$localAddresses[0]
$peerAddress=if ($localAddress -eq '192.168.1.132') { '192.168.1.131' } else { '192.168.1.132' }

$credentials=Get-PgCheckCredentials

$errors=New-Object 'Collections.Generic.List[string]'

$report=[pscustomobject]@{
    Format=1
    CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Computer=$env:COMPUTERNAME
    LocalAddress=$localAddress
    PeerAddress=$peerAddress
    Port=$Port
    SqlAccount=$credentials.Sql.UserName
    ReplicationAccount=$credentials.Replication.UserName
    Local=$null
    Peer=$null
    PeerReplication=$null
    Service=$null
    ServiceDiagnostics=$null
    PreflightPassed=$false
    Issues=@()
}

try {
    $report.Local=Get-PgSnapshot '127.0.0.1' $credentials.Sql
}
catch {
    $errors.Add('Local SQL: '+$_.Exception.Message)
}

try {
    $report.Peer=Get-PgSnapshot $peerAddress $credentials.Sql
}
catch {
    $errors.Add('Peer SQL: '+$_.Exception.Message)
}

try {
    $report.PeerReplication=Get-PgReplicationIdentity $peerAddress $credentials.Replication
}
catch {
    $errors.Add('Peer replication ('+$credentials.Replication.UserName+'): '+$_.Exception.Message)
}

if ($null -ne $report.Local) {
    try {
        $dataDirectory=[string]$report.Local.settings.data_directory.value

        $raw=& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -Mode Status -AsJson -ServiceName $ServiceName -PgBin $PgBin -DataDirectory $dataDirectory -Port $Port

        if ($LASTEXITCODE -ne 0) {
            throw 'Local service Status failed.'
        }

        $report.Service=($raw -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        $errors.Add('Local service: '+$_.Exception.Message)
    }
}

if ($null -ne $report.Service) {
    try {
        $report.ServiceDiagnostics=Get-PgServiceDiagnostics $report.Service
    }
    catch {
        $report.ServiceDiagnostics=[pscustomobject]@{
            Error=$_.Exception.Message
        }
    }
}

foreach ($issue in @(Get-PgPreflightIssues $report)) {
    $errors.Add($issue)
}

$report.Issues=$errors.ToArray()
$report.PreflightPassed=$errors.Count -eq 0
$report.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')

$path=Join-Path $OutputDirectory ('preflight-'+$localAddress+'-'+[guid]::NewGuid().ToString('N')+'.json')

[IO.File]::WriteAllText($path,($report | ConvertTo-Json -Depth 14),(New-Object Text.UTF8Encoding($true)))

$report | ConvertTo-Json -Depth 14

Write-Host ('Report: '+$path) -ForegroundColor Cyan
Write-Host 'Preflight only. No service, role, database or configuration changes were made.'