#requires -Version 5.1
[CmdletBinding()]
param([string]$RepositoryRoot='', [string]$OutputRoot='', [string]$Dotnet='dotnet', [string]$Configuration='Release', [switch]$NoArchive, [switch]$ServicesOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
if (!$RepositoryRoot) { $RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')) }
$RepositoryRoot=(Resolve-Path -LiteralPath $RepositoryRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $RepositoryRoot 'TechMES.sln') -PathType Leaf)) { throw 'TechMES.sln was not found.' }
if (!$OutputRoot) { $OutputRoot=Join-Path $RepositoryRoot 'artifacts' }
$sdks=@(& $Dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or !($sdks -match '^10\.')) { throw '.NET SDK 10 is required on the build computer.' }

# Publishes one component to a new folder; running services are not touched.
function Publish-Component([string]$Project, [string]$Destination) {
    & $Dotnet publish $Project -c $Configuration -r win-x64 --self-contained true -o $Destination -m:1 --nologo '-p:EnableWindowsTargeting=true' '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Project; exit code $LASTEXITCODE" }
}

# Returns slash-separated manifest paths independently of the build computer.
function Get-RelativeFileName([string]$Root, [string]$Path) {
    return $Path.Substring($Root.TrimEnd('\','/').Length+1).Replace('\','/')
}

# Создаёт ZIP с явными разделителями /.
# Результат не зависит от режима совместимости .NET Framework.
# При ошибке удаляется только создаваемый незавершённый архив.
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
New-Item -ItemType Directory -Path $package -Force | Out-Null
$definitions=@(
    @{Key='runtime';Project='TechMES.Runtime.Service';Folder='Runtime.Service';Service='TechMES.Runtime.Service'},
    @{Key='calc';Project='TechMES.Calc.Service';Folder='Calc.Service';Service='TechMES.Calc.Service'},
    @{Key='web';Project='TechMES.Web';Folder='Web';Service='TechMES.Web'}
)
if (!$ServicesOnly) { $definitions+=@{Key='maintenance';Project='TechMES.Maintenance';Folder='Maintenance';Service=''} }
$components=@()
foreach ($component in $definitions) {
    $project=Join-Path $RepositoryRoot ($component.Project+'/'+$component.Project+'.csproj')
    $destination=Join-Path $package $component.Folder
    Write-Output "Publishing: $($component.Project)"
    Publish-Component $project $destination
    Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Where-Object {
        $relative=Get-RelativeFileName $destination $_.FullName
        $_.Name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or $_.Extension -match '^\.(pfx|p12|pem|key|pdb|cs|csproj|sln|slnx)$' -or $relative -match '(^|/)(logs|data|certs|backups)/'
    } | Remove-Item -Force
    $exe=$component.Project+'.exe'
    if (!(Test-Path -LiteralPath (Join-Path $destination $exe) -PathType Leaf)) { throw "Executable missing: $exe" }
    $files=@(Get-ChildItem -LiteralPath $destination -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{Path=(Get-RelativeFileName $destination $_.FullName);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    $components += [ordered]@{Key=$component.Key;Folder=$component.Folder;Service=$component.Service;Exe=$exe;Files=$files}
}
[ordered]@{Format=1;Id=$id;CreatedAtUtc=[DateTime]::UtcNow.ToString('o');Runtime='win-x64';SelfContained=$true;Components=$components} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
# Упаковываем публикацию с одинаковыми разделителями на всех компьютерах.
if (!$NoArchive) {
    $zip=$package+'.zip'
    New-DeploymentArchive $package $zip
    Write-Output "ARCHIVE_PATH=$zip"
}

Write-Output "PACKAGE_ROOT=$package"
