#requires -Version 5.1
<#
.SYNOPSIS
Собирает один автономный пакет Runtime, Calc и Web для обновления серверов.
.DESCRIPTION
Запускается на компьютере разработчика с исходниками и .NET SDK 10.
В пакет не включаются исходники, настройки серверов и закрытые сертификаты.
#>
[CmdletBinding()]
param(
    # Корень исходников; по умолчанию вычисляется из scripts/deploy.
    [string]$RepositoryRoot = '',

    # Папка готовых пакетов; пустое значение означает <репозиторий>/artifacts.
    [string]$OutputRoot = '',

    # Имя dotnet в PATH либо полный путь к dotnet.exe.
    [string]$Dotnet = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# TechMES-PathResolution-v2: resolve paths after parameter binding.
$scriptFile = $PSCommandPath

if ([string]::IsNullOrWhiteSpace($scriptFile)) {
    $scriptFile = $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($scriptFile)) {
    throw 'Cannot determine the current script file.'
}

$scriptDirectory = [System.IO.Path]::GetDirectoryName($scriptFile)
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $scriptDirectory '../..')
    )
}

# Возвращает относительный путь с разделителем / для manifest.json.
function Get-RelativeFileName([string]$Root, [string]$Path) {
    return $Path.Substring($Root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
}

# Публикует один сервис в новую папку и проверяет код завершения .NET CLI.
function Publish-Component([string]$Project, [string]$Destination) {
    & $Dotnet publish $Project -c Release -r win-x64 --self-contained true -o $Destination -m:1 --nologo '-p:EnableWindowsTargeting=true' '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE : $Project"
    }
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if (!(Test-Path -LiteralPath (Join-Path $RepositoryRoot 'TechMES.sln'))) {
    throw 'Не найден TechMES.sln.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepositoryRoot 'artifacts'
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$updater = Join-Path $scriptDirectory 'Update-TechMESServer.ps1'

if (!(Test-Path -LiteralPath $updater)) {
    throw "Не найден $updater"
}

$sdks = @(& $Dotnet --list-sdks)

if ($LASTEXITCODE -ne 0 -or !($sdks -match '^10\.')) {
    throw 'Для сборки нужен .NET SDK 10.'
}

$id = 'TechMES_Update_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$package = Join-Path $OutputRoot $id

New-Item -ItemType Directory -Path $package -Force | Out-Null

$definitions = @(
    @{ Key = 'runtime'; Project = 'TechMES.Runtime.Service'; Folder = 'Runtime.Service'; Service = 'TechMES.Runtime.Service' },
    @{ Key = 'calc'; Project = 'TechMES.Calc.Service'; Folder = 'Calc.Service'; Service = 'TechMES.Calc.Service' },
    @{ Key = 'web'; Project = 'TechMES.Web'; Folder = 'Web'; Service = 'TechMES.Web' },
    @{ Key = 'maintenance'; Project = 'TechMES.Maintenance'; Folder = 'Maintenance'; Service = '' }
)

$components = @()

foreach ($component in $definitions) {
    $project = Join-Path $RepositoryRoot ($component.Project + '/' + $component.Project + '.csproj')
    $destination = Join-Path $package $component.Folder

    Write-Host "Публикация: $($component.Project)" -ForegroundColor Cyan
    Publish-Component $project $destination

    # Исключаем настройки, локальные данные, исходники и секреты
    # из результата публикации перед формированием пакета.
    Get-ChildItem -LiteralPath $destination -Recurse -File -Force |
        Where-Object {
            $relative = Get-RelativeFileName $destination $_.FullName

            $_.Name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or
            $_.Extension -match '^\.(pfx|p12|pem|key|pdb|cs|csproj|sln|slnx)$' -or
            $relative -match '(^|/)(logs|data|certs|backups)/'
        } |
        Remove-Item -Force

    $exe = $component.Project + '.exe'

    if (!(Test-Path -LiteralPath (Join-Path $destination $exe))) {
        throw "Не получен $exe"
    }

    # Manifest содержит перечень файлов и SHA256 для проверки перед обновлением.
    $files = @(
        Get-ChildItem -LiteralPath $destination -Recurse -File -Force |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    Path = (Get-RelativeFileName $destination $_.FullName)
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )

    $components += [ordered]@{
        Key = $component.Key
        Folder = $component.Folder
        Service = $component.Service
        Exe = $exe
        Files = $files
    }
}

Copy-Item -LiteralPath $updater -Destination (Join-Path $package 'Update-TechMESServer.ps1')

$manifest = [ordered]@{
    Format = 1
    Id = $id
    CreatedAtUtc = [DateTime]::UtcNow.ToString('o')
    Runtime = 'win-x64'
    SelfContained = $true
    Components = $components
}

$manifest |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8

# Архивируем только готовую публикацию со скриптом обновления и manifest.
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = $package + '.zip'
[IO.Compression.ZipFile]::CreateFromDirectory(
    $package,
    $zip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath ($zip + '.sha256') -Value $hash -Encoding ASCII

Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "SHA256: $hash"