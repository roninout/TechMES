#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$PackageRoot='', [string]$ProfileBase64='', [string]$ArchivePath='')

# Replaced only when Maintenance generates a server-specific launcher.
$script:EmbeddedProfileBase64 = ''
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)

# Tests directory equality or nesting in either direction.
function Test-PathOverlap([string]$Left, [string]$Right) {
    $a=$Left.Replace('\','/').TrimEnd('/')
    $b=$Right.Replace('\','/').TrimEnd('/')
    return $a -ieq $b -or $a.StartsWith($b+'/',[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a+'/',[StringComparison]::OrdinalIgnoreCase)
}

# Rejects links in the complete existing path.
function Assert-NoLinks([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not supported: $current" }
        }
        $current=Split-Path $current -Parent
    }
}

# Resolves a manifest path inside its root.
function Get-SafePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -match '[<>:"|?*\\\x00-\x1f]|(^|/)\.\.?(/|$)' -or @($Relative.Split('/') | Where-Object { !$_ -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or [IO.Path]::IsPathRooted($Relative)) { throw "Invalid manifest path: $Relative" }
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $path=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside its root: $Relative" }
    Assert-NoLinks $path
    return $path
}

# Protects configuration, local data and private certificates.
function Test-ProtectedPath([string]$Relative) {
    $name=[IO.Path]::GetFileName($Relative)
    return $name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or $name -match '\.(pfx|p12|pem|key)$' -or $Relative -match '(^|/)(logs|data|certs|backups)/'
}

# Gets the installation directory from the registered service image.
function Get-ServiceFolder($Service, [string]$ExpectedExe) {
    $match=[regex]::Match($Service.PathName,'^\s*(?:"([^"\r\n]+\.exe)"|(.+?\.exe))(?:\s|$)','IgnoreCase')
    if (!$match.Success) { throw "Invalid service image: $($Service.Name)" }
    $exe=if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    $exe=[Environment]::ExpandEnvironmentVariables($exe)
    if ([IO.Path]::GetFileName($exe) -ine $ExpectedExe) { throw "Unexpected executable: $exe" }
    $folder=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe))
    Assert-NoLinks $folder
    return $folder
}

# Stops a service normally and waits for its process to exit.
function Stop-Component([string]$Name) {
    $info=Get-CimInstance Win32_Service -Filter "Name='$Name'"
    $oldProcess=[int]$info.ProcessId
    $service=Get-Service -Name $Name
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $Name -ErrorAction Stop }
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(60))
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    while ($oldProcess -gt 0 -and (Get-Process -Id $oldProcess -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Service process has not exited: $Name" }
        Start-Sleep -Milliseconds 200
    }
}

# Starts the existing service without changing its account or startup mode.
function Start-Component([string]$Name) {
    if ((Get-Service -Name $Name).Status -ne 'Running') { Start-Service -Name $Name -ErrorAction Stop }
    (Get-Service -Name $Name).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(60))
}

# Accepts both Active and Standby Runtime after an update.
function Wait-RuntimeReady([string]$Url) {
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $status=Invoke-RestMethod -Uri $Url -TimeoutSec 3
            if ($status.mode -in @('Active','Standby')) { Write-Output "Runtime ready: $($status.mode)"; return }
        } catch {}
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Runtime readiness failed: $Url"
}


# Expands a ZIP into a fresh directory after checking every entry and its manifest.
function Expand-DeploymentArchive([string]$Archive, [string]$Destination, [string]$ManifestHash) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen=@{}
        foreach ($entry in $zip.Entries) {
            $relative=$entry.FullName.TrimEnd('/')
            if (!$relative -or $seen.ContainsKey($relative) -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960) { throw "Invalid or duplicate ZIP entry: $($entry.FullName)" }
            $seen[$relative]=$true
            $null=Get-SafePath $Destination $relative
        }
        $manifestEntry=$zip.GetEntry('manifest.json')
        if (!$manifestEntry) { throw 'manifest.json is missing from the ZIP.' }
        $stream=$manifestEntry.Open()
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ine $ManifestHash) { throw 'The ZIP does not match this deployment script. Copy the ZIP and script from the same prepared folder.' }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive,$Destination)
}

# Restores files touched by this transaction; unrelated files and settings are preserved.
function Restore-Files($Entries) {
    foreach ($entry in $Entries) {
        if (!$entry.Touched) { continue }
        if ($entry.Existed) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force }
        elseif (Test-Path -LiteralPath $entry.Target) { Remove-Item -LiteralPath $entry.Target -Force }
    }
}

# Stops only installed service components; Maintenance is updated only while closed.
function Stop-DeploymentJobs($Jobs) {
    foreach ($key in @('web','calc','runtime')) {
        foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key -and ($_.Existed -or $_.Created) })) { Stop-Component $job.Name }
    }
}

# Removes only a service created by the failed transaction.
function Remove-CreatedService([string]$Name) {
    & "$env:SystemRoot\System32\sc.exe" delete $Name | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Cannot remove a newly created service: $Name" }
}

# Applies files, registers missing services, starts selected services and rolls back on failure.
function Invoke-UpdateTransaction($Jobs, $Entries, $Configs, [string]$StatusUrl) {
    try {
        Stop-DeploymentJobs $Jobs
        foreach ($entry in $Entries) {
            $entry.Touched=$true
            if ($entry.Delete) { Remove-Item -LiteralPath $entry.Target -Force; continue }
            New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
            if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ine $entry.Hash) { throw "Installed file hash mismatch: $($entry.Target)" }
        }
        foreach ($job in @($Jobs | Where-Object { $_.Name -and !$_.Existed })) {
            New-Service -Name $job.Name -BinaryPathName ('"'+$job.Executable+'"') -DisplayName $job.Name -StartupType Automatic -ErrorAction Stop | Out-Null
            $job.Created=$true
        }
        foreach ($key in @('runtime','calc','web')) {
            foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key })) { Start-Component $job.Name }
        }
        if (@($Jobs | Where-Object { $_.Key -eq 'runtime' }).Count -gt 0) { Wait-RuntimeReady $StatusUrl }
        foreach ($job in @($Jobs | Where-Object { $_.Name })) {
            if ([string](Get-Service -Name $job.Name).Status -ne 'Running') { throw "Unexpected service state: $($job.Name)" }
        }
        foreach ($config in $Configs) {
            if ((Get-FileHash -LiteralPath $config.Path -Algorithm SHA256).Hash -ine $config.Hash) { throw "Configuration changed: $($config.Path)" }
        }
    } catch {
        $original=$_.Exception.Message
        Write-Warning "Update failed; restoring files and service states: $original"
        try {
            if (@($Entries | Where-Object { $_.Touched }).Count -gt 0) { Stop-DeploymentJobs $Jobs; Restore-Files $Entries }
            foreach ($job in @($Jobs | Where-Object { $_.Created })) {
                Remove-CreatedService $job.Name
            }
            foreach ($key in @('runtime','calc','web')) {
                foreach ($job in @($Jobs | Where-Object { $_.Key -eq $key -and $_.WasRunning })) { Start-Component $job.Name }
            }
            foreach ($job in @($Jobs | Where-Object { $_.Existed -and $_.Name })) {
                $expected=if ($job.WasRunning) { 'Running' } else { 'Stopped' }
                if ([string](Get-Service -Name $job.Name).Status -ne $expected) { throw "Rollback state mismatch: $($job.Name)" }
            }
        } catch { throw "UPDATE FAILED: $original; ROLLBACK INCOMPLETE: $($_.Exception.Message)" }
        throw "Update rolled back: $original"
    }
}

# Checks the complete plan and makes backups before stopping any service.
function Invoke-LocalDeployment($Profile, [string]$PayloadRoot, $Caller) {
    $manifestPath=Join-Path $PayloadRoot 'manifest.json'
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ine $Profile.ManifestSha256) { throw 'Manifest hash mismatch.' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64') { throw 'Unsupported package format or platform.' }
    $allowed=@{
        runtime=@{Service='TechMES.Runtime.Service';Exe='TechMES.Runtime.Service.exe';Folder='Runtime.Service'}
        calc=@{Service='TechMES.Calc.Service';Exe='TechMES.Calc.Service.exe';Folder='Calc.Service'}
        web=@{Service='TechMES.Web';Exe='TechMES.Web.exe';Folder='Web'}
        maintenance=@{Service='';Exe='TechMES.Maintenance.exe';Folder='Maintenance'}
    }
    $install=[IO.Path]::GetFullPath($Profile.InstallRoot).TrimEnd('\')
    Assert-NoLinks $install
    $backup=Join-Path $env:ProgramData ('TechMES\Updates\'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N'))
    Assert-NoLinks $backup
    if ((Test-PathOverlap $PayloadRoot $install) -or (Test-PathOverlap $backup $install) -or (Test-PathOverlap $backup $PayloadRoot)) { throw 'Installation, package and backup paths must be separate.' }
    $jobs=@(); $entries=@(); $configs=@(); $selected=@{}
    foreach ($key in $Profile.Components) {
        if (!$allowed.ContainsKey($key) -or $selected.ContainsKey($key)) { throw "Invalid or duplicate component: $key" }
        $selected[$key]=$true
        $parts=@($manifest.Components | Where-Object { $_.Key -ceq $key })
        if ($parts.Count -ne 1) { throw "Component missing or duplicated: $key" }
        $part=$parts[0]
        if ($part.Service -cne $allowed[$key].Service -or $part.Exe -cne $allowed[$key].Exe -or $part.Folder -cne $allowed[$key].Folder) { throw "Invalid component definition: $key" }
        $source=Get-SafePath $PayloadRoot $part.Folder
        $target=Get-SafePath $install $part.Folder
        $service=$null
        if ($part.Service) {
            $service=Get-CimInstance Win32_Service -Filter "Name='$($part.Service)'"
            if ($service) {
                if ($service.State -notin @('Running','Stopped')) { throw "Service is not stable: $($service.Name)" }
                if ((Get-ServiceFolder $service $part.Exe) -ine $target) { throw "Profile InstallRoot does not match registered service $($part.Service). Correct the profile; no service was stopped." }
            }
            if (!(Test-Path -LiteralPath (Join-Path $target 'appsettings.json') -PathType Leaf)) { throw "Prepare server-specific appsettings.json first: $target" }
        } else {
            foreach ($process in @(Get-Process -Name 'TechMES.Maintenance' -ErrorAction SilentlyContinue)) {
                if ([IO.Path]::GetDirectoryName($process.MainModule.FileName) -ieq $target) { throw 'Close Maintenance on this computer before running its deployment script.' }
            }
        }
        $seen=@{}
        foreach ($file in $part.Files) {
            if ((Test-ProtectedPath $file.Path) -or $seen.ContainsKey($file.Path)) { throw "Protected or duplicate payload path: $($file.Path)" }
            $seen[$file.Path]=$true
            $from=Get-SafePath $source $file.Path
            $to=Get-SafePath $target $file.Path
            if (Test-Path -LiteralPath $to -PathType Container) { throw "A directory blocks a payload file: $to" }
            if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ine $file.Sha256) { throw "Package hash mismatch: $key/$($file.Path)" }
            $entries += [pscustomobject]@{Source=$from;Target=$to;Hash=$file.Sha256;Backup=(Get-SafePath (Join-Path $backup $key) $file.Path);Existed=(Test-Path -LiteralPath $to -PathType Leaf);Touched=$false;Delete=$false}
        }
        if (!$seen.ContainsKey($part.Exe)) { throw "Executable missing from manifest: $($part.Exe)" }
        if ($key -eq 'maintenance') {
            foreach ($obsolete in @('Tools/Deploy-TechMESSecondary.ps1','Tools/Run-TechMESUpdate.ps1')) {
                $oldPath=Get-SafePath $target $obsolete
                if (!$seen.ContainsKey($obsolete) -and (Test-Path -LiteralPath $oldPath -PathType Leaf)) {
                    $entries += [pscustomobject]@{Source='';Target=$oldPath;Hash='';Backup=(Get-SafePath (Join-Path $backup $key) $obsolete);Existed=$true;Touched=$false;Delete=$true}
                }
            }
        }
        $jobs += [pscustomobject]@{Key=$key;Name=$part.Service;Folder=$target;Executable=(Join-Path $target $part.Exe);Existed=($null -ne $service);Created=$false;WasRunning=($null -ne $service -and $service.State -eq 'Running')}
        if (Test-Path -LiteralPath $target -PathType Container) {
            $configs += @(Get-ChildItem -LiteralPath $target -Filter 'appsettings*.json' -File -Force | ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 })
        }
    }
    $jobs | Format-Table Key,Name,Folder,WasRunning -AutoSize | Out-Host
    if (!$Caller.ShouldProcess($env:COMPUTERNAME,"Deploy $($manifest.Id) to $install")) { return }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $jobs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'services.json') -Encoding UTF8
    $configs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'settings-hashes.json') -Encoding UTF8
    foreach ($entry in $entries) {
        if ($entry.Existed) {
            New-Item -ItemType Directory -Path (Split-Path $entry.Backup -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Target -Destination $entry.Backup -Force
        }
    }
    Write-Output "Backup: $backup"
    Invoke-UpdateTransaction $jobs $entries $configs $Profile.RuntimeStatusUrl
    Write-Output 'SUCCESS. Selected services are running. Existing appsettings files were preserved.'
}

$exitCode=0; $mutex=$null; $locked=$false; $transcript=$false; $extracted=$false
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
    if (!$ProfileBase64) { $ProfileBase64=$script:EmbeddedProfileBase64 }
    if (!$ProfileBase64) { throw 'Use Deploy-Primary.ps1 or Deploy-Secondary.ps1 generated by Maintenance.' }
    $profile=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ProfileBase64)) | ConvertFrom-Json
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { $admin=(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
    if (!$admin) {
        if (!$script:EmbeddedProfileBase64) { throw 'Run Maintenance as administrator.' }
        $command="& '"+$PSCommandPath.Replace("'","''")+"'"
        if ($ArchivePath) { $command+=" -ArchivePath '"+$ArchivePath.Replace("'","''")+"'" }
        if ($PackageRoot) { $command+=" -PackageRoot '"+$PackageRoot.Replace("'","''")+"'" }
        if ($WhatIfPreference) { $command+=' -WhatIf' }
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $child=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -Wait -PassThru
        exit $child.ExitCode
    }
    if (![Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell.' }
    $addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
    if ($profile.ComputerName -ine $env:COMPUTERNAME -and $profile.ComputerName -notin $addresses) { throw "Wrong computer. This script is for $($profile.Name): $($profile.ComputerName)." }
    if ($profile.InstallRoot -notmatch '^[A-Za-z]:\\[^\\]') { throw 'A local installation directory is required.' }
    if ($profile.InstallRoot.Substring(3) -match '[<>:"|?*/\x00-\x1f]' -or @($profile.InstallRoot.Substring(3).Split('\') | Where-Object { !$_ -or $_ -in @('.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0) { throw 'Invalid installation path.' }
    $uri=$null
    if (![Uri]::TryCreate($profile.RuntimeStatusUrl,[UriKind]::Absolute,[ref]$uri) -or !$uri.IsLoopback -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'Runtime status URL must be local to this computer.' }
    if (@($profile.Components).Count -eq 0) { throw 'Select at least one component.' }
    if ($profile.ManifestSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid manifest fingerprint.' }
    $mutex=New-Object Threading.Mutex($false,'Global\TechMES.ServerUpdate')
    try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (!$locked) { throw 'Another TechMES deployment is already running.' }
    $jobRoot=Join-Path $env:ProgramData ('TechMES\DeploymentRuns\'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N'))
    Assert-NoLinks $jobRoot
    if (Test-PathOverlap $jobRoot $profile.InstallRoot) { throw 'Installation directory overlaps the deployment working directory.' }
    New-Item -ItemType Directory -Path $jobRoot -Force -WhatIf:$false | Out-Null
    $logPath=Join-Path $jobRoot 'deployment.log'
    Start-Transcript -LiteralPath $logPath -Force | Out-Null
    $transcript=$true
    Write-Output "Profile: $($profile.Name); Computer: $env:COMPUTERNAME; InstallRoot: $($profile.InstallRoot)"
    Write-Output "Log: $logPath"
    if (!$PackageRoot) {
        if (!$ArchivePath) { $ArchivePath=Join-Path $PSScriptRoot $profile.ArchiveName }
        if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
            $PackageRoot=Join-Path $jobRoot 'package'
            Expand-DeploymentArchive $ArchivePath $PackageRoot $profile.ManifestSha256
            $extracted=$true
        } elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -PathType Leaf) {
            $PackageRoot=$PSScriptRoot
        } else { throw 'Place this script beside TechMES_Deployment.zip, or run the script from the extracted package.' }
    }
    $PackageRoot=(Resolve-Path -LiteralPath $PackageRoot).Path
    Assert-NoLinks $PackageRoot
    Invoke-LocalDeployment $profile $PackageRoot $PSCmdlet
    if ($extracted) {
        try { Remove-Item -LiteralPath $PackageRoot -Recurse -Force -WhatIf:$false }
        catch { Write-Warning ("Deployment finished; temporary payload could not be removed: "+$_.Exception.Message) }
    }
} catch {
    $exitCode=1
    Write-Host ('FAILED: '+$_.Exception.Message) -ForegroundColor Red
} finally {
    if ($transcript) { Stop-Transcript | Out-Null }
    if ($locked) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
if ($script:EmbeddedProfileBase64) { try { $null=Read-Host 'Press ENTER to close' } catch {} }
exit $exitCode
