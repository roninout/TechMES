#requires -Version 5.1
<#
.SYNOPSIS
Обновляет установленные службы TechMES из готового пакета без исходников и SDK.
.DESCRIPTION
Сохраняет appsettings*.json и прежнее состояние запуска служб.
До замены создаёт резервную копию затрагиваемых файлов.
При ошибке пытается восстановить файлы и прежнее состояние служб.
PostgreSQL не останавливает.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    # Распакованный пакет с manifest.json и опубликованными приложениями.
    [string]$PackageRoot = '',

    # Обновляем выбранные компоненты, которые уже установлены на этом ПК.
    [ValidateSet('runtime', 'calc', 'web')]
    [string[]]$Components = @('runtime', 'calc', 'web'),

    # Резервные копии файлов должны находиться вне папок приложений.
    [string]$BackupRoot = "$env:ProgramData\TechMES\Updates",

    # Проверка локального Runtime; Standby также является допустимым результатом.
    [string]$RuntimeStatusUrl = 'http://127.0.0.1:5101/api/runtime/status'
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
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = $scriptDirectory
}

# Разрешает только относительный путь внутри заданной папки.
# Обход через ../, абсолютный путь, junction или symlink запрещён.
function Get-SafePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -match '[:\\]|(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($Relative)) {
        throw "Недопустимый путь: $Relative"
    }

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $full = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))

    if (!$full.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Путь за пределами папки: $Relative"
    }

    $current = $full

    while ($current -and $current.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Обнаружена ссылка/junction: $current"
            }
        }

        $current = Split-Path $current -Parent
    }

    return $full
}

# Не допускает замену настроек, данных и закрытых сертификатов,
# даже если такой файл ошибочно указан в manifest.
function Test-ProtectedPath([string]$Relative) {
    $name = [IO.Path]::GetFileName($Relative)

    return $name -match '^(appsettings.*\.json|maintenance\.settings\.json|user-state\.json)$' -or
        $name -match '\.(pfx|p12|pem|key)$' -or
        $Relative -match '(^|/)(logs|data|certs|backups)/'
}

# Получает каталог из действительного binPath службы.
# Параметры запуска, учётная запись и тип запуска службы не изменяются.
function Get-ServiceFolder($Service, [string]$ExpectedExe) {
    $match = [regex]::Match(
        $Service.PathName,
        '^\s*(?:"([^"\r\n]+\.exe)"|(.+?\.exe))(?:\s|$)',
        'IgnoreCase'
    )

    if (!$match.Success) {
        throw "Не удалось разобрать binPath службы $($Service.Name)."
    }

    $exePath = if ($match.Groups[1].Success) {
        $match.Groups[1].Value
    } else {
        $match.Groups[2].Value
    }

    $exePath = [Environment]::ExpandEnvironmentVariables($exePath)

    if ([IO.Path]::GetFileName($exePath) -ine $ExpectedExe) {
        throw "Неожиданный exe службы $($Service.Name): $exePath"
    }

    return Split-Path ([IO.Path]::GetFullPath($exePath)) -Parent
}

# Штатно останавливает службу и ожидает завершения её процесса.
# Принудительное завершение процесса не используется.
function Stop-Component([string]$Name) {
    $info = Get-CimInstance Win32_Service -Filter "Name='$Name'"
    $processId = [int]$info.ProcessId
    $service = Get-Service -Name $Name

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -ErrorAction Stop
    }

    $service.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(60)
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(60)

    while ($processId -gt 0 -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -gt $deadline) {
            throw "Процесс службы $Name ещё не завершился."
        }

        Start-Sleep -Milliseconds 200
    }
}

# Запускает службу и ожидает подтверждения Running от Windows.
function Start-Component([string]$Name) {
    Start-Service -Name $Name -ErrorAction Stop

    (Get-Service -Name $Name).WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(60)
    )
}

# Сохраняет старые версии только тех файлов, которые будут заменены.
# Настройки отсутствуют в этом списке и не копируются из пакета.
function Backup-UpdateFiles($Entries) {
    foreach ($entry in $Entries) {
        if ($entry.Existed) {
            New-Item -ItemType Directory -Path (Split-Path $entry.Backup -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Target -Destination $entry.Backup -Force
        }
    }
}

# Заменяет файлы из проверенного manifest и проверяет результат записи.
# Touched отмечается до копирования, чтобы откатить также незавершённую замену.
function Install-UpdateFiles($Entries) {
    foreach ($entry in $Entries) {
        New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent) -Force | Out-Null

        $entry.Touched = $true
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force

        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash) {
            throw "Ошибка проверки записанного файла: $($entry.Target)"
        }
    }
}

# При откате восстанавливает прежние файлы.
# Файлы, впервые добавленные текущим обновлением, удаляет.
function Restore-UpdateFiles($Entries) {
    foreach ($entry in $Entries) {
        if (!$entry.Touched) {
            continue
        }

        if ($entry.Existed) {
            Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force
        } elseif (Test-Path -LiteralPath $entry.Target) {
            Remove-Item -LiteralPath $entry.Target -Force
        }
    }
}

# Ожидает доступность локального Runtime.
# Active и Standby одинаково допустимы для успешного обновления.
function Wait-RuntimeReady([string]$Url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)

    do {
        try {
            $status = Invoke-RestMethod -Uri $Url -TimeoutSec 3

            if ($status.mode -in @('Active', 'Standby')) {
                Write-Host "Runtime: $($status.mode)"
                return
            }
        } catch {
        }

        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Runtime не подтвердил Active/Standby: $Url"
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Этот скрипт запускается на Windows-сервере.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()

if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запусти PowerShell от имени администратора.'
}

$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'manifest.json') -Raw | ConvertFrom-Json

if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64' -or !$manifest.SelfContained) {
    throw 'Неподдерживаемый формат пакета.'
}

if (![Environment]::Is64BitOperatingSystem) {
    throw 'Для пакета нужна Windows x64.'
}

$allowed = @{
    runtime = @{ Service = 'TechMES.Runtime.Service'; Exe = 'TechMES.Runtime.Service.exe' }
    calc = @{ Service = 'TechMES.Calc.Service'; Exe = 'TechMES.Calc.Service.exe' }
    web = @{ Service = 'TechMES.Web'; Exe = 'TechMES.Web.exe' }
}

$jobs = @()
$entries = @()
$configHashes = @()

$stamp = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$backup = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) $stamp

$mutex = New-Object Threading.Mutex($false, 'Global\TechMES.ServerUpdate')
$locked = $false

try {
    try {
        $locked = $mutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $locked = $true
    }

    if (!$locked) {
        throw 'На этом компьютере уже выполняется обновление TechMES.'
    }

    # Проверяем весь план до остановки первой службы.
    foreach ($key in ($Components | Select-Object -Unique)) {
        $parts = @($manifest.Components | Where-Object { $_.Key -eq $key })

        if ($parts.Count -ne 1) {
            throw "В пакете должно быть одно описание компонента $key."
        }

        $part = $parts[0]

        if ($part.Service -cne $allowed[$key].Service -or $part.Exe -cne $allowed[$key].Exe) {
            throw "Некорректное описание $key."
        }

        $service = Get-CimInstance Win32_Service -Filter "Name='$($part.Service)'"

        if (!$service) {
            Write-Host "Пропуск: $($part.Service) не установлен."
            continue
        }

        if ($service.State -notin @('Running', 'Stopped')) {
            throw "Нестабильное состояние $($service.Name): $($service.State)"
        }

        $target = Get-ServiceFolder $service $part.Exe
        $source = Get-SafePath $PackageRoot $part.Folder

        if (!(Test-Path -LiteralPath (Join-Path $target 'appsettings.json') -PathType Leaf)) {
            throw "Нет существующих настроек $target\appsettings.json. Пакет предназначен для обновления."
        }

        $targetPrefix = $target.TrimEnd('\') + '\'

        if ($PackageRoot.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase) -or $PackageRoot -ieq $target -or $backup.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Пакет и резервные копии должны лежать вне папки приложения.'
        }

        $seen = @{}

        foreach ($file in $part.Files) {
            if (Test-ProtectedPath $file.Path) {
                throw "В пакете обнаружен защищённый файл: $($file.Path)"
            }

            if ($seen.ContainsKey($file.Path)) {
                throw "Повтор файла: $($file.Path)"
            }

            $seen[$file.Path] = $true

            $from = Get-SafePath $source $file.Path
            $to = Get-SafePath $target $file.Path

            if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ine $file.Sha256) {
                throw "Не совпадает SHA256: $key/$($file.Path)"
            }

            $entries += [pscustomobject]@{
                Source = $from
                Target = $to
                Backup = (Get-SafePath (Join-Path $backup $key) $file.Path)
                Existed = (Test-Path -LiteralPath $to -PathType Leaf)
                Touched = $false
            }
        }

        if (!$seen.ContainsKey($part.Exe)) {
            throw "В manifest отсутствует $($part.Exe)."
        }

        $jobs += [pscustomobject]@{
            Key = $key
            Name = $part.Service
            Folder = $target
            WasRunning = ($service.State -eq 'Running')
        }

        # Запоминаем хеши настроек для итоговой проверки сохранности.
        $configHashes += @(
            Get-ChildItem -LiteralPath $target -Filter 'appsettings*.json' -File -Force |
                ForEach-Object {
                    Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                }
        )
    }

    if ($jobs.Count -eq 0) {
        throw 'Не найдены установленные службы для обновления.'
    }

    if (@($jobs.Folder | Sort-Object -Unique).Count -ne $jobs.Count) {
        throw 'У служб совпадают папки установки.'
    }

    $jobs | Format-Table Key, Name, Folder, WasRunning -AutoSize | Out-Host

    # -WhatIf заканчивает работу здесь: файлы и службы остаются без изменений.
    if (!$PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Обновление TechMES из $($manifest.Id)")) {
        return
    }

    New-Item -ItemType Directory -Path $backup -Force | Out-Null

    $jobs |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $backup 'services.json') -Encoding UTF8

    $configHashes |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $backup 'settings-hashes.json') -Encoding UTF8

    Backup-UpdateFiles $entries

    try {
        # Сначала останавливаем клиентов, затем Runtime.
        foreach ($key in @('web', 'calc', 'runtime')) {
            foreach ($job in @($jobs | Where-Object { $_.Key -eq $key })) {
                Stop-Component $job.Name
            }
        }

        Install-UpdateFiles $entries

        # Запускаем только те службы, которые работали до обновления.
        foreach ($key in @('runtime', 'calc', 'web')) {
            foreach ($job in @($jobs | Where-Object { $_.Key -eq $key -and $_.WasRunning })) {
                Start-Component $job.Name
            }
        }

        if (@($jobs | Where-Object { $_.Key -eq 'runtime' -and $_.WasRunning }).Count -gt 0) {
            Wait-RuntimeReady $RuntimeStatusUrl
        }

        foreach ($job in $jobs) {
            $expected = if ($job.WasRunning) { 'Running' } else { 'Stopped' }

            if ([string](Get-Service -Name $job.Name).Status -ne $expected) {
                throw "Изменилось состояние $($job.Name)."
            }
        }

        foreach ($config in $configHashes) {
            if ((Get-FileHash -LiteralPath $config.Path -Algorithm SHA256).Hash -ne $config.Hash) {
                throw "Изменились настройки $($config.Path)."
            }
        }
    }
    catch {
        $original = $_.Exception.Message
        Write-Warning "Обновление не завершено: $original. Выполняется откат файлов."

        try {
            # Если замены уже начались, перед откатом снова останавливаем приложения.
            if (@($entries | Where-Object { $_.Touched }).Count -gt 0) {
                foreach ($key in @('web', 'calc', 'runtime')) {
                    foreach ($job in @($jobs | Where-Object { $_.Key -eq $key })) {
                        Stop-Component $job.Name
                    }
                }

                Restore-UpdateFiles $entries
            }

            foreach ($key in @('runtime', 'calc', 'web')) {
                foreach ($job in @($jobs | Where-Object { $_.Key -eq $key -and $_.WasRunning })) {
                    Start-Component $job.Name
                }
            }
        }
        catch {
            throw "Ошибка обновления: $original. Откат не завершён: $($_.Exception.Message). Резервная копия: $backup"
        }

        throw "Обновление отменено; файлы и состояние запуска восстановлены. Причина: $original. Резервная копия: $backup"
    }

    Write-Host "Обновление завершено. appsettings*.json сохранены. Резервная копия: $backup" -ForegroundColor Green
}
finally {
    if ($locked) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}