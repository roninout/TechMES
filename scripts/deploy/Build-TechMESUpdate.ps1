#requires -Version 5.1
[CmdletBinding()]
param([string]$RepositoryRoot='', [string]$OutputRoot='', [string]$Dotnet='', [string]$Configuration='Release', [switch]$NoArchive, [switch]$ServicesOnly, [switch]$SdkOnly, [switch]$HostOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)

# Keep this script ASCII-only for Windows PowerShell 5.1 without a UTF-8 BOM.
# Finds an SDK without depending on the PATH inherited by Visual Studio.
function Resolve-DotnetSdk([string]$Requested) {
    $candidates = New-Object 'System.Collections.Generic.List[string]'

    if ($Requested) {
        if (Test-Path -LiteralPath $Requested -PathType Leaf) {
            $candidates.Add((Resolve-Path -LiteralPath $Requested).ProviderPath)
        }
        else {
            $command = Get-Command -Name $Requested -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $command) { throw "dotnet executable was not found: $Requested" }
            $candidates.Add($command.Source)
        }
    }
    else {
        foreach ($name in @('DOTNET_ROOT_X64', 'ProgramW6432', 'ProgramFiles', 'DOTNET_ROOT', 'USERPROFILE')) {
            $root = [Environment]::GetEnvironmentVariable($name)
            if (!$root) { continue }
            if ($name -eq 'ProgramW6432' -or $name -eq 'ProgramFiles') { $root = Join-Path $root 'dotnet' }
            if ($name -eq 'USERPROFILE') { $root = Join-Path $root '.dotnet' }
            $candidates.Add((Join-Path $root 'dotnet.exe'))
        }

        foreach ($command in @(Get-Command -Name dotnet -CommandType Application -All -ErrorAction SilentlyContinue)) {
            $candidates.Add($command.Source)
        }
    }

    $seen = @{}
    $oldLanguage = [Environment]::GetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'Process')

    try {
        [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'en', 'Process')

        foreach ($candidate in $candidates) {
            if ($seen.ContainsKey($candidate)) { continue }
            $seen[$candidate] = $true
            if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

            Write-Host "Checking SDK: $candidate"

            try {
                $versions = @(& $candidate --list-sdks 2>&1)
                $exitCode = $LASTEXITCODE
            }
            catch {
                Write-Host ("SDK probe failed: " + $_.Exception.Message)
                continue
            }

            $hasSdk = $false
            foreach ($version in $versions) {
                $line = [string]$version
                Write-Host "  $line"
                if ($line -match '^\s*(\d+)\.') {
                    if ([int]$Matches[1] -ge 10) { $hasSdk = $true }
                }
            }

            if ($exitCode -eq 0 -and $hasSdk) { return $candidate }
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', $oldLanguage, 'Process')
    }

    throw '.NET SDK 10 or newer was not found. Install the x64 SDK on the build computer or specify -Dotnet with its full executable path.'
}

$Dotnet=Resolve-DotnetSdk $Dotnet
if ($SdkOnly) { Write-Output "DOTNET_EXE=$Dotnet"; return }

if (!$RepositoryRoot) { $RepositoryRoot=Join-Path $PSScriptRoot '../..' }
$RepositoryRoot=(Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath

if (!(Test-Path -LiteralPath (Join-Path $RepositoryRoot 'TechMES.sln') -PathType Leaf)) {
    throw 'TechMES.sln was not found.'
}

if (!$OutputRoot) { $OutputRoot=Join-Path $RepositoryRoot 'artifacts' }

# Publishes one component to a new folder; running services are not touched.
function Publish-Component([string]$Project, [string]$Destination) {
    & $Dotnet publish $Project -c $Configuration -r win-x64 --self-contained true -o $Destination --artifacts-path $buildRoot -m:1 --nologo '-p:EnableWindowsTargeting=true' '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $Project; exit code $LASTEXITCODE"
    }
}

# Returns slash-separated manifest paths independently of the build computer.
function Get-RelativeFileName([string]$Root, [string]$Path) {
    return $Path.Substring($Root.TrimEnd('\','/').Length+1).Replace('\','/')
}

# Creates a ZIP with slash separators; removes only its own incomplete archive.
function New-DeploymentArchive([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stream=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)

    try {
        $archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)

        try {
            foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
                $relative=Get-RelativeFileName $Source $file.FullName
                $entry=$archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
                $sourceStream=[IO.File]::OpenRead($file.FullName)

                try {
                    $entryStream=$entry.Open()

                    try {
                        $sourceStream.CopyTo($entryStream)
                    }
                    finally {
                        $entryStream.Dispose()
                    }
                }
                finally {
                    $sourceStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    catch {
        $stream.Dispose()
        [IO.File]::Delete($Destination)
        throw
    }
    finally {
        $stream.Dispose()
    }
}

$id='TechMES_Update_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6)
$package=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $id
$buildRoot=$package+'.build'

New-Item -ItemType Directory -Path $package -Force | Out-Null

$definitions=@(
    @{Key='runtime';Project='TechMES.Runtime.Service';Folder='Runtime.Service';Service='TechMES.Runtime.Service'},
    @{Key='calc';Project='TechMES.Calc.Service';Folder='Calc.Service';Service='TechMES.Calc.Service'},
    @{Key='web';Project='TechMES.Web';Folder='Web';Service='TechMES.Web'},
    @{Key='host';Project='TechMES.Host';Folder='Host';Service='TechMES.Host'}
)

if ($HostOnly) {
    $definitions=@($definitions | Where-Object { $_.Key -eq 'host' })
}
elseif (!$ServicesOnly) {
    $definitions+=@{Key='maintenance';Project='TechMES.Maintenance';Folder='Maintenance';Service=''}
}

$components=@()

foreach ($component in $definitions) {
    $project=Join-Path $RepositoryRoot ($component.Project+'/'+$component.Project+'.csproj')
    $destination=Join-Path $package $component.Folder

    Write-Output "Publishing: $($component.Project)"

    Publish-Component $project $destination

    Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Where-Object {
        $relative=Get-RelativeFileName $destination $_.FullName

        $_.Name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or
        $_.Extension -match '^\.(pfx|p12|pem|key|pdb|cs|csproj|sln|slnx)$' -or
        $relative -match '(^|/)(logs|data|certs|backups)/'
    } | Remove-Item -Force

    $exe=$component.Project+'.exe'

    if (!(Test-Path -LiteralPath (Join-Path $destination $exe) -PathType Leaf)) {
        throw "Executable missing: $exe"
    }

    $files=@(
        Get-ChildItem -LiteralPath $destination -Recurse -File -Force |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    Path=(Get-RelativeFileName $destination $_.FullName)
                    Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )

    $components += [ordered]@{
        Key=$component.Key
        Folder=$component.Folder
        Service=$component.Service
        Exe=$exe
        Files=$files
    }
}

[ordered]@{
    Format=1
    Id=$id
    CreatedAtUtc=[DateTime]::UtcNow.ToString('o')
    Runtime='win-x64'
    SelfContained=$true
    Components=$components
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8

# Remove only this invocation's isolated build files after successful publishing.
if (Test-Path -LiteralPath $buildRoot -PathType Container) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

if (!$NoArchive) {
    $zip=$package+'.zip'
    New-DeploymentArchive $package $zip
    Write-Output "ARCHIVE_PATH=$zip"
}

Write-Output "PACKAGE_ROOT=$package"