#requires -Version 5.1
[CmdletBinding()]
param([string]$RepositoryRoot = '', [string]$OutputRoot = '', [string]$Dotnet = '', [string]$Configuration = 'Release', [switch]$NoArchive)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

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

# Returns package paths with slash separators for the manifest and ZIP.
function Get-RelativeFileName([string]$Root, [string]$Path) {
    return $Path.Substring($Root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
}

# Creates a new ZIP and removes only that incomplete ZIP if packaging fails.
function New-AgentArchive([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)

    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)

        try {
            foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
                $relative = Get-RelativeFileName $Source $file.FullName
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $sourceStream = [IO.File]::OpenRead($file.FullName)

                try {
                    $entryStream = $entry.Open()
                    try { $sourceStream.CopyTo($entryStream) }
                    finally { $entryStream.Dispose() }
                }
                finally { $sourceStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    catch {
        $stream.Dispose()
        if ([IO.File]::Exists($Destination)) { [IO.File]::Delete($Destination) }
        throw
    }
    finally { $stream.Dispose() }
}

if (!$RepositoryRoot) { $RepositoryRoot = Join-Path $PSScriptRoot '../..' }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath
$project = Join-Path $RepositoryRoot 'TechMES.PostgreSql.Agent/TechMES.PostgreSql.Agent.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Agent project was not found: $project" }

if (!$OutputRoot) { $OutputRoot = Join-Path $RepositoryRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$dotnetExe = Resolve-DotnetSdk $Dotnet
$id = 'TechMES_PostgreSqlAgent_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$packageRoot = Join-Path $OutputRoot $id
$agentRoot = Join-Path $packageRoot 'PostgreSql.Agent'
$buildRoot = Join-Path $OutputRoot ($id + '.build')
[void][IO.Directory]::CreateDirectory($agentRoot)

Write-Host "Publishing PostgreSQL Agent using: $dotnetExe"
& $dotnetExe publish $project -c $Configuration -r win-x64 --self-contained true -o $agentRoot --artifacts-path $buildRoot -m:1 --nologo '-p:EnableWindowsTargeting=true' '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed with exit code $LASTEXITCODE. Build files: $buildRoot" }

# Exclude computer-specific settings, credentials and development files.
Get-ChildItem -LiteralPath $agentRoot -Recurse -File -Force | Where-Object {
    $relative = Get-RelativeFileName $agentRoot $_.FullName
    $_.Name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json|credentials\.bin)$' -or
    $_.Name -match '^(Repair-TechMESPostgreSqlFence|Test-PostgreSQL18Switchover)\.ps1$' -or
    $_.Extension -match '^\.(pfx|p12|pem|key|cer|crt|pdb|cs|csproj|sln|slnx)$' -or
    $relative -match '(^|/)(logs|data|certs|backups)/'
} | Remove-Item -Force

foreach ($required in @('TechMES.PostgreSql.Agent.exe', 'TechMES.PostgreSql.Control.dll', 'Tools/Install-TechMESPostgreSqlHost.ps1')) {
    if (!(Test-Path -LiteralPath (Join-Path $agentRoot $required) -PathType Leaf)) { throw "Required package file is missing: $required" }
}

$files = @(Get-ChildItem -LiteralPath $agentRoot -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = (Get-RelativeFileName $agentRoot $_.FullName); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})

$component = [ordered]@{
    Key = 'postgres-agent'
    Folder = 'PostgreSql.Agent'
    Service = 'TechMES.PostgreSql.Agent'
    Exe = 'TechMES.PostgreSql.Agent.exe'
    Files = $files
}

[ordered]@{
    Format = 1
    Id = $id
    CreatedAtUtc = [DateTime]::UtcNow.ToString('o')
    Runtime = 'win-x64'
    SelfContained = $true
    Components = @($component)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8

# Remove only this invocation's isolated build output after a successful publish.
if (Test-Path -LiteralPath $buildRoot -PathType Container) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }

if (!$NoArchive) {
    $archivePath = $packageRoot + '.zip'
    New-AgentArchive $packageRoot $archivePath
    Write-Output "ARCHIVE_PATH=$archivePath"
}

Write-Output "PACKAGE_ROOT=$packageRoot"
Write-Host 'DONE. Copy this package to the database server and extract it.'
Write-Host 'Run Tools/Install-TechMESPostgreSqlHost.ps1 from the extracted PostgreSql.Agent folder.'