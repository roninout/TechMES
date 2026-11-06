#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Controller','Worker')][string]$Mode='Controller', [string]$PlanPath='', [string]$ComputerName='', [string]$ArchivePath='', [string]$ShareRoot='', [string]$StageRoot='', [string]$InstallRoot='', [string]$RuntimeStatusUrl='http://127.0.0.1:5101/api/runtime/status', [string]$Components='runtime,calc,web', [string]$LogPath='', [string]$CredentialPath='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$utf8=New-Object Text.UTF8Encoding($false)

# Appends UTF-8 without using the console code page.
function Write-DeployLog([string]$Text) {
    $line='['+(Get-Date -Format 'HH:mm:ss')+'] '+$Text+[Environment]::NewLine
    $bytes=$utf8.GetBytes($line)
    $stream=[IO.File]::Open($LogPath,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
    try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    if ($Mode -eq 'Controller') { Write-Output $line.TrimEnd() }
}

# Publishes a complete state file atomically.
function Write-JobState([string]$Path, [string]$Phase, [string]$Message) {
    $text=[ordered]@{Phase=$Phase;Message=$Message;ChangedAtUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Compress
    $tmp=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($tmp,$text,$utf8)
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($tmp,$Path,[Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($tmp,$Path) }
}

# Runs under SYSTEM on Secondary independently of the Primary connection.
function Invoke-UpdateWorker([string]$Path) {
    $folder=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    $script:LogPath=Join-Path $folder 'remote.log'
    $statePath=Join-Path $folder 'status.json'

    try {
        $plan=Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($plan.Format -ne 1 -or $plan.ComputerName -ine $env:COMPUTERNAME -or [IO.Path]::GetFullPath($plan.Folder) -ine $folder) { throw 'Wrong target computer or job directory.' }

        $updater=Join-Path $folder 'Update-TechMESServer.ps1'
        if ((Get-FileHash -LiteralPath $updater -Algorithm SHA256).Hash -ine $plan.UpdaterHash) { throw 'Updater hash changed.' }

        Write-JobState $statePath 'Running' 'Updating selected services.'
        Write-DeployLog 'Updating selected TechMES services.'

        & $updater -PackageRoot $plan.Payload -Components @($plan.Components) -ExpectedInstallRoot $plan.InstallRoot -RuntimeStatusUrl $plan.RuntimeStatusUrl -StartAll *>&1 | ForEach-Object { Write-DeployLog ($_ | Out-String).TrimEnd() }

        if (!$?) { throw 'Updater did not succeed.' }

        Write-DeployLog 'SUCCESS: selected services are running; appsettings were preserved.'
        Write-JobState $statePath 'Succeeded' 'Selected services are running.'
        return 0
    } catch {
        Write-DeployLog ('FAILED: '+$_.Exception.Message)
        Write-JobState $statePath 'Failed' $_.Exception.Message
        return 1
    }
}

# Self-contained remote preflight; secures a dedicated staging root.
function New-RemoteDeploymentFolder {
    param([string]$Stage, [string]$Installed, [string]$Selected, [string]$Id, [string]$PrimaryName)

    $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'

    if ($env:COMPUTERNAME -ieq $PrimaryName) { throw 'Secondary points to Primary.' }
    if (![Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required.' }

    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Remote administrator rights are required.' } } finally { $identity.Dispose() }

    foreach ($name in @('Get-CimInstance','New-ScheduledTaskAction','New-ScheduledTaskPrincipal','New-ScheduledTaskSettingsSet','Register-ScheduledTask','Start-ScheduledTask','Get-ScheduledTask','Get-ScheduledTaskInfo','Unregister-ScheduledTask')) { [void](Get-Command $name -ErrorAction Stop) }

    $stagePath=[IO.Path]::GetFullPath($Stage).TrimEnd('\')
    $installedPath=[IO.Path]::GetFullPath($Installed).TrimEnd('\')

    if ($stagePath -ieq $installedPath -or $stagePath.StartsWith($installedPath+'\',[StringComparison]::OrdinalIgnoreCase) -or $installedPath.StartsWith($stagePath+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installation and staging directories must be separate.' }

    foreach ($directory in @($stagePath,$installedPath)) {
        $current=$directory
        while ($current) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Directory links are not supported: $current" }
            $current=Split-Path $current -Parent
        }
    }

    if (!(Test-Path -LiteralPath $installedPath -PathType Container)) { throw 'Installation root is missing.' }

    $names=@{runtime='TechMES.Runtime.Service';calc='TechMES.Calc.Service';web='TechMES.Web'}
    foreach ($key in $Selected.Split(',')) {
        $service=Get-CimInstance Win32_Service -Filter "Name='$($names[$key])'"
        if (!$service -or $service.State -notin @('Running','Stopped')) { throw "Selected service is missing or unstable: $($names[$key])" }
    }

    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null

    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)

    foreach ($sidText in @('S-1-5-18','S-1-5-32-544')) {
        $sid=New-Object Security.Principal.SecurityIdentifier($sidText)
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $stagePath -AclObject $acl

    $folder=Join-Path $stagePath $Id
    if (Test-Path -LiteralPath $folder) { throw 'Job directory already exists.' }

    New-Item -ItemType Directory -Path $folder | Out-Null
    [pscustomobject]@{ComputerName=$env:COMPUTERNAME;Folder=$folder;InstallRoot=$installedPath}
}

# Self-contained remote verification and extraction; WhatIf changes no services.
function Prepare-RemotePayload {
    param([string]$Folder, [string]$ArchiveHash, [string]$UpdaterHash, [string]$WorkerHash)

    $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'

    foreach ($pair in @(@('package.zip',$ArchiveHash),@('Update-TechMESServer.ps1',$UpdaterHash),@('Deploy-TechMESSecondary.ps1',$WorkerHash))) {
        if ((Get-FileHash -LiteralPath (Join-Path $Folder $pair[0]) -Algorithm SHA256).Hash -ine $pair[1]) { throw "Delivery hash mismatch: $($pair[0]). Check UNC/local paths." }
    }

    $plan=Get-Content -LiteralPath (Join-Path $Folder 'plan.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $payload=Join-Path $Folder 'Payload'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=Join-Path $Folder 'package.zip'
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)

    try {
        foreach ($entry in $zip.Entries) {
            $name=$entry.FullName.Replace('\','/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)') { throw "Invalid ZIP path: $name" }
        }
    } finally { $zip.Dispose() }

    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$payload)

    & (Join-Path $Folder 'Update-TechMESServer.ps1') -PackageRoot $payload -Components @($plan.Components) -ExpectedInstallRoot $plan.InstallRoot -RuntimeStatusUrl $plan.RuntimeStatusUrl -StartAll -WhatIf *>&1 | Out-String | Write-Output
}

# Starts an on-demand task with no automatic restart or time-based trigger.
function Start-RemoteDeployment {
    param([string]$Folder, [string]$TaskName)

    $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
    $worker=(Join-Path $Folder 'Deploy-TechMESSecondary.ps1').Replace("'","''")
    $plan=(Join-Path $Folder 'plan.json').Replace("'","''")
    $command="& '$worker' -Mode Worker -PlanPath '$plan'"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $exe=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

    $action=New-ScheduledTaskAction -Execute $exe -Argument "-NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -DisallowHardTerminate -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}

# Reads complete progress lines while allowing concurrent writes/replacements.
function Get-RemoteDeploymentState {
    param([string]$Folder, [string]$TaskName)

    $ErrorActionPreference='Stop'

    function Read-SharedText([string]$Path) {
        if (!(Test-Path -LiteralPath $Path)) { return '' }
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }

    $text=Read-SharedText (Join-Path $Folder 'remote.log')
    $end=$text.LastIndexOf([char]10)
    $text=if ($end -ge 0) { $text.Substring(0,$end+1) } else { '' }

    $status=Read-SharedText (Join-Path $Folder 'status.json')
    $state=if ($status) { $status | ConvertFrom-Json } else { [pscustomobject]@{Phase='Queued';Message='Waiting for task.'} }
    $taskState=[string](Get-ScheduledTask -TaskName $TaskName).State

    [pscustomobject]@{Phase=$state.Phase;Message=$state.Message;Log=$text;TaskActive=($taskState -in @('Running','Queued','4','2'));TaskResult=(Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult}
}

# Controls the full deployment from Primary.
function Invoke-SecondaryDeployment {
    $keys=@($Components -split '[,;]' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ } | Select-Object -Unique)

    if (!$keys.Count -or @($keys | Where-Object { $_ -notin @('runtime','calc','web') }).Count) { throw 'Select runtime, calc and/or web.' }

    $shareMatch=[regex]::Match($ShareRoot,'^\\\\([^\\/]+)\\')
    if ($ComputerName -notmatch '^[A-Za-z0-9_.-]+$' -or !$shareMatch.Success -or $shareMatch.Groups[1].Value -ine $ComputerName) { throw 'ComputerName and UNC server must match.' }
    if ($StageRoot -notmatch '^[A-Za-z]:\\.+$' -or $InstallRoot -notmatch '^[A-Za-z]:\\.+$') { throw 'Use local drive paths on Secondary.' }
    if (!(Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw 'Select the ready ZIP package.' }

    $uri=[Uri]$RuntimeStatusUrl
    if (!$uri.IsAbsoluteUri -or !$uri.IsLoopback -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'Runtime URL must be local to Secondary.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($ArchivePath)

    try {
        $entry=$zip.GetEntry('manifest.json')
        if (!$entry) { throw 'ZIP has no root manifest.json.' }

        $reader=New-Object IO.StreamReader($entry.Open(),[Text.Encoding]::UTF8,$true)
        try { $manifest=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }

        if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64' -or !$manifest.SelfContained) { throw 'Unsupported ZIP package.' }
    } finally { $zip.Dispose() }

    $scriptFile=$PSCommandPath
    $updater=Join-Path ([IO.Path]::GetDirectoryName($scriptFile)) 'Update-TechMESServer.ps1'
    if (!(Test-Path -LiteralPath $updater)) { throw 'Updater is missing from Maintenance/Tools.' }

    $id='TechMES_'+[Guid]::NewGuid().ToString('N')
    $taskName='TechMESUpdate-'+[Guid]::NewGuid().ToString('N')
    $session=$null; $driveName=''
    $credential=$null

    if ($CredentialPath) {
        $credential=Import-Clixml -LiteralPath $CredentialPath
        if ($credential -isnot [Management.Automation.PSCredential]) { throw 'Credential file is invalid.' }
    }

    try {
        Write-DeployLog "Connecting to $ComputerName through WinRM."

        $sessionArgs=@{ComputerName=$ComputerName;ErrorAction='Stop'}
        if ($credential) { $sessionArgs.Credential=$credential }

        $session=New-PSSession @sessionArgs
        $remote=Invoke-Command -Session $session -ScriptBlock (Get-Command New-RemoteDeploymentFolder).ScriptBlock -ArgumentList $StageRoot,$InstallRoot,($keys -join ','),$id,$env:COMPUTERNAME

        Write-DeployLog "Remote result: $($remote.Folder)\status.json"

        if ($credential) {
            $driveName='TD'+[Guid]::NewGuid().ToString('N').Substring(0,8)
            New-PSDrive -Name $driveName -PSProvider FileSystem -Root $ShareRoot -Credential $credential | Out-Null
            $share=Join-Path ($driveName+':\') $id
        } else {
            $share=Join-Path $ShareRoot $id
        }

        $archiveHash=(Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
        $updaterHash=(Get-FileHash -LiteralPath $updater -Algorithm SHA256).Hash
        $workerHash=(Get-FileHash -LiteralPath $scriptFile -Algorithm SHA256).Hash

        New-Item -ItemType Directory -Path $share -Force | Out-Null
        Copy-Item -LiteralPath $ArchivePath -Destination (Join-Path $share 'package.zip')
        Copy-Item -LiteralPath $updater -Destination (Join-Path $share 'Update-TechMESServer.ps1')
        Copy-Item -LiteralPath $scriptFile -Destination (Join-Path $share 'Deploy-TechMESSecondary.ps1')

        $plan=[ordered]@{Format=1;ComputerName=$remote.ComputerName;Folder=$remote.Folder;Payload=(Join-Path $remote.Folder 'Payload');InstallRoot=$remote.InstallRoot;Components=@($keys);RuntimeStatusUrl=$RuntimeStatusUrl;UpdaterHash=$updaterHash;TaskName=$taskName}
        $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $share 'plan.json') -Encoding UTF8

        Write-DeployLog 'Copied. Verifying before any service is stopped.'

        $preflight=Invoke-Command -Session $session -ScriptBlock (Get-Command Prepare-RemotePayload).ScriptBlock -ArgumentList $remote.Folder,$archiveHash,$updaterHash,$workerHash
        Write-DeployLog ($preflight | Out-String).TrimEnd()

        Write-DeployLog "Starting task: $taskName"
        Invoke-Command -Session $session -ScriptBlock (Get-Command Start-RemoteDeployment).ScriptBlock -ArgumentList $remote.Folder,$taskName
        Write-DeployLog "Task started: $taskName"

        $deadline=[DateTime]::UtcNow.AddMinutes(30)
        $offset=0; $idleCount=0; $phase=''

        do {
            Start-Sleep -Seconds 2

            $state=Invoke-Command -Session $session -ScriptBlock (Get-Command Get-RemoteDeploymentState).ScriptBlock -ArgumentList $remote.Folder,$taskName

            if ($state.Log.Length -gt $offset) {
                Write-DeployLog $state.Log.Substring($offset).TrimEnd()
                $offset=$state.Log.Length
            }

            if ($state.Phase -ne $phase) {
                Write-DeployLog ("Secondary: "+$state.Phase)
                $phase=$state.Phase
            }

            if ($state.Phase -eq 'Succeeded') {
                try {
                    Invoke-Command -Session $session -ScriptBlock { param($name) if ([string](Get-ScheduledTask -TaskName $name).State -notin @('Running','Queued','4','2')) { Unregister-ScheduledTask -TaskName $name -Confirm:$false } } -ArgumentList $taskName
                } catch {
                    Write-DeployLog 'Update succeeded; task cleanup was not confirmed.'
                }

                Write-DeployLog 'SUCCESS: Deploy Secondary completed.'
                return
            }

            if ($state.Phase -eq 'Failed') { throw $state.Message }
            if (!$state.TaskActive) { $idleCount++ } else { $idleCount=0 }

            if ($idleCount -ge 30) { throw "No terminal job state. LastTaskResult=$($state.TaskResult). Check remote.log." }
        } while ([DateTime]::UtcNow -lt $deadline)

        throw 'Wait limit reached. Remote task was NOT terminated; inspect status.json before repeating.'
    } finally {
        if ($driveName) { Remove-PSDrive -Name $driveName -Force -ErrorAction SilentlyContinue }
        if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
    }
}

try {
    if ($Mode -eq 'Worker') { exit (Invoke-UpdateWorker $PlanPath) }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
    if (!$LogPath) { throw 'A Primary log path is required.' }

    Invoke-SecondaryDeployment
    exit 0
} catch {
    if ($LogPath) { Write-DeployLog ('NOT CONFIRMED: '+$_.Exception.Message) }
    exit 1
}