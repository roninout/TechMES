#requires -Version 5.1
<#
.SYNOPSIS
Добавляет комментарии к методам блока 4.1 и убирает /32 из вывода IP.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = ''
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

# Добавляет XML-комментарий перед методом.
# Повторный запуск пропускает уже добавленное описание.
function Add-MethodComment([string]$Text, [string]$Member, [string]$Description) {
    if ($Text.Contains($Description)) {
        return $Text
    }

    if ([regex]::Matches($Text, [regex]::Escape($Member)).Count -ne 1) {
        throw "Неоднозначный или отсутствующий метод: $Member"
    }

    $comment = "    /// <summary>`r`n    /// $Description`r`n    /// </summary>`r`n"
    return $Text.Replace($Member, $comment + $Member)
}

$changes = @()

$path = Join-Path $RepositoryRoot 'TechMES.Infrastructure.PostgreSql/Connections/PostgreSqlClusterMonitor.cs'
$text = [IO.File]::ReadAllText($path)

$text = $text.Replace(
    'SELECT inet_server_addr()::text, inet_server_port(), pg_is_in_recovery(),',
    'SELECT host(inet_server_addr()), inet_server_port(), pg_is_in_recovery(),'
)

$text = Add-MethodComment $text '    Task<PostgreSqlServerState> ProbeAsync' 'Читает роль и состояние репликации одного сервера с общим ограничением времени.'
$text = Add-MethodComment $text '    public async Task<PostgreSqlServerState> ProbeAsync' 'Проверяет соединение, затем отдельно читает диагностические данные. Недостаток прав на диагностику не превращает доступную БД в Offline.'
$text = Add-MethodComment $text '    private static string? Text' 'Читает необязательное текстовое поле PostgreSQL, сохраняя SQL NULL как null.'
$text = Add-MethodComment $text '    public PostgreSqlClusterMonitor(' 'Запоминает существующие подключения Primary/Secondary и общий Health Period. Соединения в конструкторе не открываются.'
$text = Add-MethodComment $text '    public PostgreSqlClusterHealthResponse GetHealth(' 'Возвращает готовый снимок без SQL. Просроченные сведения помечает Stale.'
$text = Add-MethodComment $text '    public async Task RefreshAsync(' 'Параллельно проверяет оба сервера и атомарно публикует общий результат. Повторный цикл пропускается, пока предыдущий ещё выполняется.'
$text = Add-MethodComment $text '    private async Task<PostgreSqlServerState> CheckAsync(' 'Проверяет настроенный сервер и обновляет его независимые счётчики соединения. Пустое резервное подключение не опрашивается.'
$text = Add-MethodComment $text '    private static string Describe(' 'Описывает наблюдаемую топологию и обнаруженные противоречия. Результат не является разрешением на повышение реплики.'

$changes += @{ Path = $path; Text = $text }

$path = Join-Path $RepositoryRoot 'TechMES.Runtime.Service/Workers/PlantScadaHealthWorker.cs'
$text = [IO.File]::ReadAllText($path)

$text = Add-MethodComment $text '    protected override async Task ExecuteAsync(' 'Запускает независимые проверки по одному существующему таймеру. Монитор PostgreSQL работает также в Standby; при остановке ожидаем завершения текущих циклов.'
$text = Add-MethodComment $text '    private async Task CheckClusterAsync(' 'Обновляет диагностику PostgreSQL вне разрешения на бизнес-операции. Ошибка проверки не завершает health worker.'
$text = Add-MethodComment $text '    private async Task CheckScadaAsync(' 'Проверяет SCADA только в действующей сессии Active. При утрате активности отменяет запрос и освобождает разрешение.'

$changes += @{ Path = $path; Text = $text }

# Все изменения сначала проверены в памяти.
# Резервные копии сохраняем в игнорируемую Git папку artifacts.
$backupRoot = Join-Path $RepositoryRoot ('artifacts/cluster-comments-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

foreach ($change in $changes) {
    $backup = Join-Path $backupRoot ([IO.Path]::GetFileName($change.Path))
    Copy-Item -LiteralPath $change.Path -Destination $backup

    [IO.File]::WriteAllText(
        $change.Path,
        $change.Text,
        (New-Object Text.UTF8Encoding($true))
    )
}

Write-Host 'Комментарии добавлены. IP выводится без /32.' -ForegroundColor Green