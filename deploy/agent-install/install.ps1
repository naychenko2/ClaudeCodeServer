<#
.SYNOPSIS
    Установщик агента устройства (ADR-016, agent-distribution Р10).
.DESCRIPTION
    Скачивает архив агента с сервера по /agent/manifest.json, проверяет размер и SHA-256,
    распаковывает в %LOCALAPPDATA%\AiHomeAgent\versions\{version}, вызывает
    `ai-home-agent install` с теми же аргументами.

    Запускается строкой:
      powershell -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm https://S/agent/install.ps1))) -Server https://S -Code КОД [-Name ...]"

    Без прав администратора. Ошибка — понятный текст по-русски, ненулевой код, мусор убран.
    Повторный запуск поверх установленного агента его не ломает: новый архив ложится в
    новый versions/{v}, активный указатель не трогаем (это дело команды install).
.PARAMETER Server
    URL сервера (например, https://home.example.com).
.PARAMETER Code
    Код сопряжения из UI.
.PARAMETER Name
    Имя устройства (по умолчанию — $env:COMPUTERNAME).
.PARAMETER Help
    Показать справку и выйти.
.NOTES
    Совместимость: PowerShell 5.1+ (Windows PowerShell). Без PS6/7-only командлетов
    (SkipHttpErrorCheck, ConvertTo-Json -AsHashtable, ternary '?:'). Системные вызовы —
    через [System.Net.Http.HttpClient], который работает одинаково во всех версиях.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Code,
    [string]$Name,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

# ---------- коды возврата ----------
$ExitOk        = 0
$ExitUsage     = 2
$ExitDownload  = 3
$ExitIntegrity = 4
$ExitTool      = 5
$ExitInstall   = 10

# ---------- вывод ----------
function Write-Info([string]$msg) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor Gray
}
function Write-Warn([string]$msg) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor DarkYellow
}
function Write-Bad([string]$msg) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor Red
}
function Write-Good([string]$msg) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor Green
}

function Exit-With([int]$code, [string]$msg) {
    if ($code -eq $ExitOk) { Write-Good $msg } else { Write-Bad "ошибка: $msg" }
    exit $code
}

function Show-Usage {
    @"
Установщик агента AI Home (Windows, PowerShell 5.1+).

Использование:
    powershell -ExecutionPolicy Bypass -File install.ps1 -Server URL -Code КОД [-Name ИМЯ] [-Help]

Аргументы:
    -Server URL     адрес сервера (например, https://home.example.com)
    -Code КОД       код сопряжения из UI
    -Name ИМЯ       имя устройства (по умолчанию — $env:COMPUTERNAME)

Каталог данных агента:
    %LOCALAPPDATA%\AiHomeAgent\versions\{version}

Коды возврата: 0 — успех (в том числе «запустится при следующем входе» и
                «сервер пока не принял» — с предупреждением), 2 — аргументы, 3 — сервер,
                4 — целостность архива, 5 — нет инструмента, 10 — install вернул ошибку.
"@
}

if ($Help) { Show-Usage; exit $ExitOk }

# ---------- URL без хвостового слэша ----------
if ($Server.EndsWith('/')) { $Server = $Server.TrimEnd('/') }

# ---------- канал: https или петля ----------
# До любой загрузки: по открытому http атакующий в сети подменит архив, и чужой бинарь
# выполнится раньше, чем сервер откажет в сопряжении. Правило то же, что у сервера
# (DeviceChannelGuard) и агента (ServerChannel): https, либо http на петле.
function Test-SecureServer([string]$url) {
    $uri = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)) { return $false }
    if ($uri.Scheme -eq 'https') { return $true }
    return ($uri.Scheme -eq 'http' -and $uri.IsLoopback)
}

if (-not (Test-SecureServer $Server)) {
    Exit-With $ExitUsage ("адрес сервера должен быть https:// (http допустим только для localhost, 127.0.0.1 и [::1]): '$Server'. " +
        "Откройте веб-интерфейс по https-адресу и скопируйте команду оттуда")
}

# Редирект не должен опускать протокол: ответ, пришедший не с запрошенного адреса, годится только по https
function Assert-SecureResponse($resp, [string]$url) {
    # Упавший запрос PowerShell отдаёт из .Result как $null — это сетевой отказ, его разбирает catch
    if ($null -eq $resp) { throw "сервер не ответил" }
    $final = $resp.RequestMessage.RequestUri
    if ($final.AbsoluteUri -ne ([Uri]$url).AbsoluteUri -and $final.Scheme -ne 'https') {
        Exit-With $ExitDownload "сервер перенаправил на незащищённый адрес $final — загрузка остановлена"
    }
}

# RID — только win-x64
$Rid = 'win-x64'

# ---------- временный каталог ----------
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ccs-agent-install-" + [guid]::NewGuid().ToString('N'))
if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    # Не Windows (прогон тестов под pwsh): ACL Windows тут нет, у GetTempPath свои права
    $null = New-Item -ItemType Directory -Path $TempDir
} else {
    # Доступ только текущему пользователю, без наследования от %TEMP%: соседний процесс
    # не подменит архив между проверкой SHA-256 и распаковкой. Каталог создаётся сразу с ACL
    $Acl = New-Object System.Security.AccessControl.DirectorySecurity
    $Acl.SetAccessRuleProtection($true, $false)
    $Me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $Acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Me, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $null = [System.IO.FileSystemAclExtensions]::Create([System.IO.DirectoryInfo]::new($TempDir), $Acl)
    } else {
        $null = [System.IO.Directory]::CreateDirectory($TempDir, $Acl)
    }
}

# ---------- скачивание через HttpClient (одинаково в PS 5.1 и 7) ----------
# Invoke-WebRequest в PS 5.1 плохо обрабатывает тело ошибки при 5xx; HttpClient всегда
# даёт и код, и тело, и формат не зависит от версии PS.
Add-Type -AssemblyName System.Net.Http

function New-HttpClient([int]$timeoutSeconds) {
    # Timeout задаётся только здесь, до первого запроса: в .NET Framework (PS 5.1) смена
    # свойств HttpClient после первого запроса бросает InvalidOperationException
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
    return $client
}

$HttpClient = New-HttpClient 30

function Get-Manifest([string]$url, [string]$localPath) {
    # Возвращает PSCustomObject с .StatusCode (int) и .Body (string).
    try {
        $resp = $HttpClient.GetAsync($url).Result
        Assert-SecureResponse $resp $url
        $body = $resp.Content.ReadAsStringAsync().Result
        return [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Body = $body }
    } catch {
        # Таймаут, разрыв соединения, DNS, TLS — всё сюда
        Write-Bad "HTTP-запрос к $url провалился: $($_.Exception.Message)"
        Exit-With $ExitDownload "манифест недоступен: $url"
    }
}

function Get-Archive([string]$url, [string]$localPath) {
    # Таймаут на скачивание архива — 5 минут; агент 70–100 МБ. Отдельный клиент: таймаут
    # клиента манифеста после его запроса уже не поменять
    $archiveClient = New-HttpClient 300
    try {
        $resp = $archiveClient.GetAsync($url).Result
        Assert-SecureResponse $resp $url
        $bytes = $resp.Content.ReadAsByteArrayAsync().Result
        if (-not $resp.IsSuccessStatusCode) {
            # Попробуем разобрать как JSON для нормального сообщения
            try {
                $errJson = $bytes | ConvertFrom-Json -ErrorAction SilentlyContinue
                if ($errJson -and $errJson.error) {
                    Write-Bad "сервер ответил $($resp.StatusCode): $($errJson.error)"
                    Exit-With $ExitDownload "архив не отдан: $($errJson.error)"
                }
            } catch { }
            Write-Bad "сервер ответил $($resp.StatusCode): $([System.Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min($bytes.Length, 200)))"
            Exit-With $ExitDownload "архив недоступен: $url"
        }
        [System.IO.File]::WriteAllBytes($localPath, $bytes)
    } catch {
        Exit-With $ExitDownload "не удалось скачать архив: $url ($($_.Exception.Message))"
    } finally {
        $archiveClient.Dispose()
    }
}

# ---------- манифест ----------
$ManifestUrl = "$Server/agent/manifest.json"
$ManifestPath = Join-Path $TempDir 'manifest.json'
Write-Info "манифест: $ManifestUrl"
$ManifestResp = Get-Manifest $ManifestUrl $ManifestPath
if ($ManifestResp.StatusCode -ne 200) {
    # 503 → JSON {"error":"..."} от контроллера раздачи
    $reason = $null
    try {
        $err = $ManifestResp.Body | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($err -and $err.error) { $reason = $err.error }
    } catch { }
    if ($reason) {
        Exit-With $ExitDownload "сервер не раздаёт агента: $reason"
    }
    Exit-With $ExitDownload "манифест вернул $($ManifestResp.StatusCode): $($ManifestResp.Body)"
}

[System.IO.File]::WriteAllText($ManifestPath, $ManifestResp.Body, [System.Text.UTF8Encoding]::new($false))

try {
    $Manifest = $ManifestResp.Body | ConvertFrom-Json -ErrorAction Stop
} catch {
    Exit-With $ExitDownload "манифест — не валидный JSON"
}

# ---------- поля манифеста ----------
$Version = "$($Manifest.version)"
if (-not $Version) { Exit-With $ExitDownload "манифест не содержит version" }

$ArchiveEntry = $Manifest.archives.$Rid
if (-not $ArchiveEntry) { Exit-With $ExitDownload "манифест не содержит архив для $Rid" }

$ArchiveFile = "$($ArchiveEntry.file)"
$ArchiveSize = [int64]$ArchiveEntry.size
$ArchiveSha  = "$($ArchiveEntry.sha256)".ToLowerInvariant()

if (-not $ArchiveFile)              { Exit-With $ExitDownload "манифест не содержит file" }
if ($ArchiveSize -le 0)             { Exit-With $ExitDownload "манифест: size не положительное ($ArchiveSize)" }
if ($ArchiveSha -notmatch '^[0-9a-f]{64}$') {
    Exit-With $ExitDownload "манифест: sha256 не 64 hex ('$ArchiveSha')"
}
if ($ArchiveFile -match '[\\/]')    { Exit-With $ExitDownload "манифест: имя архива содержит разделитель пути ('$ArchiveFile')" }

# ---------- скачиваем архив ----------
$ArchiveLocal = Join-Path $TempDir $ArchiveFile
$ArchiveUrl = "$Server/agent/$Version/$Rid/$ArchiveFile"
Write-Info "скачиваю: $Version/$Rid/$ArchiveFile ($ArchiveSize байт)"
Get-Archive $ArchiveUrl $ArchiveLocal

# ---------- размер ----------
$GotSize = (Get-Item -LiteralPath $ArchiveLocal).Length
if ($GotSize -ne $ArchiveSize) {
    Exit-With $ExitIntegrity "размер скачанного ($GotSize) не совпадает с манифестом ($ArchiveSize)"
}

# ---------- SHA-256 ----------
$GotSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArchiveLocal).Hash.ToLowerInvariant()
if ($GotSha -ne $ArchiveSha) {
    Exit-With $ExitIntegrity "SHA-256 скачанного ($GotSha) не совпадает с манифестом ($ArchiveSha)"
}

# ---------- распаковка ----------
$LocalAppData = [Environment]::GetFolderPath('LocalApplicationData')
if (-not $LocalAppData) { $LocalAppData = $env:LOCALAPPDATA }
if (-not $LocalAppData) { Exit-With $ExitTool "не задан %LOCALAPPDATA% — некуда положить агента" }

$VersionsDir = Join-Path $LocalAppData 'AiHomeAgent\versions'
$VersionDir = Join-Path $VersionsDir $Version
Write-Info "распаковка: $VersionDir"
$null = New-Item -ItemType Directory -Path $VersionDir -Force

# Expand-Archive -Force спокойно перезатирает существующее — это и есть «поверх
# установленного агента не ломает»: новый архив ложится в новый versions/{v},
# активный указатель не трогаем (это дело команды install).
try {
    Expand-Archive -Path $ArchiveLocal -DestinationPath $VersionDir -Force -ErrorAction Stop
} catch {
    Exit-With $ExitIntegrity "не удалось распаковать архив: $($_.Exception.Message)"
}

# ---------- бинарь ----------
$AgentBin = Join-Path $VersionDir 'ai-home-agent.exe'
if (-not (Test-Path -LiteralPath $AgentBin)) {
    Exit-With $ExitTool "бинарь $AgentBin не найден после распаковки"
}

# ---------- запуск install ----------
# Аргументы агента — в его синтаксисе (--server), а не в синтаксисе параметров PowerShell
$InstallArgs = @('install', '--server', $Server, '--code', $Code)
if ($Name) { $InstallArgs += @('--name', $Name) }

Write-Info "$AgentBin $($InstallArgs -join ' ')"

try {
    # PassThru + WaitForExit + Handle: иначе ExitCode приходит пустым у уже
    # завершившегося процесса (см. deploy-agent.ps1 — та же грабля с $proc.Handle).
    $proc = Start-Process -FilePath $AgentBin -ArgumentList $InstallArgs -NoNewWindow -PassThru -ErrorAction Stop
    $null = $proc.Handle
    $null = $proc.WaitForExit()
} catch {
    Exit-With $ExitInstall "не удалось запустить ai-home-agent install: $($_.Exception.Message)"
}

# 20 и 21 — агент установлен, но есть что сказать человеку (коды InstallExitCodes агента)
switch ($proc.ExitCode) {
    0 { }
    20 { Write-Warn "агент установлен, но сейчас не запущен: окно установки не отпускает дочерние процессы. Он запустится сам при следующем входе в систему" }
    21 { Write-Warn "агент запущен, но сервер его пока не принял; агент продолжит попытки сам. Проверьте раздел «Устройства» в веб-интерфейсе" }
    default { Exit-With $ExitInstall "ai-home-agent install вернул код $($proc.ExitCode)" }
}

Write-Good "готово: $VersionDir"
exit $ExitOk
