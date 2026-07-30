<#
.SYNOPSIS
    Идемпотентный импортёр правил алертов SigNoz из JSON-файлов репозитория.

.DESCRIPTION
    Близнец dashboards/apply.ps1, но для /api/v1/rules. Source of truth — JSON в репе,
    SigNoz — applied-копия: после `docker compose down -v` один прогон возвращает правила.

    Идемпотентность: GET /api/v1/rules → матч по полю `alert` (имя правила) →
    PUT если найдено, POST если новое. Повторный запуск дублей не плодит.

    ВАЖНО: SigNoz отказывается создавать правило без канала уведомлений
    («at least one channel is required»). Канал указывается в поле preferredChannels
    самого правила; скрипт заранее проверяет, что такой канал существует, и внятно
    сообщает, если нет — иначе ошибка приходит на каждом файле по отдельности.

.PARAMETER SignozUrl
    Базовый URL SigNoz. По умолчанию http://localhost:3301.

.PARAMETER Jwt
    Service Account key / PAT / JWT. По умолчанию $env:SIGNOZ_JWT (или файл кред).

.PARAMETER AlertsDir
    Папка с *.json. По умолчанию — та, где лежит скрипт.

.EXAMPLE
    .\apply-alerts.ps1

.NOTES
    Схема правила снята с DTO SigNoz v0.134; разбор — docs/observability/overview.md, раздел «Алертинг».
#>
[CmdletBinding()]
param(
    [string]$SignozUrl = $(if ($env:SIGNOZ_URL) { $env:SIGNOZ_URL } else { 'http://localhost:3301' }),
    [string]$Jwt = $env:SIGNOZ_JWT,
    [string]$AlertsDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

# ── 0. Креды из .signoz-credentials.ps1 (рядом или в родительской папке) ───────
if (-not $Jwt) {
    $credPaths = @(
        (Join-Path $PSScriptRoot '.signoz-credentials.ps1'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) '.signoz-credentials.ps1')
    )
    foreach ($p in $credPaths) {
        if (Test-Path $p) {
            . $p
            if ($env:SIGNOZ_JWT) { $Jwt = $env:SIGNOZ_JWT }
            if ($env:SIGNOZ_URL) { $SignozUrl = $env:SIGNOZ_URL }
            break
        }
    }
}
if (-not $Jwt) { throw "Нужен -Jwt или `$env:SIGNOZ_JWT (см. .signoz-credentials.example.ps1)" }

# Заголовки: SIGNOZ-API-KEY универсален; Bearer добавляем только настоящему JWT —
# при обоих сразу SigNoz предпочитает Authorization и Service Account key падает с 401.
$isJwt = $Jwt.Split('.').Count -eq 3
$headers = @{ 'SIGNOZ-API-KEY' = $Jwt; 'Content-Type' = 'application/json; charset=utf-8' }
if ($isJwt) { $headers['Authorization'] = "Bearer $Jwt" }

# Те же заголовки для curl — через stdin, чтобы ключ не светился в командной строке
# процесса (её видит любой процесс того же пользователя).
$curlConfig = @("header = `"SIGNOZ-API-KEY: $Jwt`"", 'header = "Content-Type: application/json; charset=utf-8"')
if ($isJwt) { $curlConfig += "header = `"Authorization: Bearer $Jwt`"" }
$curlConfig = $curlConfig -join "`n"

function Invoke-SignozApi([string]$Method, [string]$Uri, [string]$Config, [string]$File) {
    # HTTP-код проверяем ЯВНО: curl без -f возвращает 0 и при 4xx/5xx, из-за чего
    # протухший ключ выглядел бы как успешный импорт.
    $raw = $Config | & curl.exe -sS -X $Method $Uri --config - --data-binary "@$File" -w "`n%{http_code}" 2>&1
    $text = ($raw | Out-String).TrimEnd("`r", "`n")
    $lines = $text -split "`r?`n"
    $code = $lines[-1]
    $payload = if ($lines.Count -gt 1) { ($lines[0..($lines.Count - 2)] -join "`n") } else { '' }
    [pscustomobject]@{
        Ok      = ($LASTEXITCODE -eq 0 -and $code -match '^2\d\d$')
        Code    = $code
        Payload = $payload
    }
}

# ── 1. Существующие правила и каналы ──────────────────────────────────────────
Write-Host "→ Получение списка правил..." -ForegroundColor Cyan
try {
    $existing = Invoke-RestMethod -Uri "$SignozUrl/api/v1/rules" -Headers $headers -TimeoutSec 10
}
catch {
    throw "GET /api/v1/rules не удался: $($_.Exception.Message). Проверь ключ и URL."
}
$existingRules = @($existing.data.rules)
Write-Host "✓ Правил в SigNoz: $($existingRules.Count)" -ForegroundColor Green

$channelNames = @()
try {
    $channels = Invoke-RestMethod -Uri "$SignozUrl/api/v1/channels" -Headers $headers -TimeoutSec 10
    $channelNames = @($channels.data | ForEach-Object { $_.name })
}
catch {
    Write-Host "! Список каналов получить не удалось — проверка привязки пропущена" -ForegroundColor Yellow
}

# ── 2. Применяем каждый файл ──────────────────────────────────────────────────
$files = @(Get-ChildItem -Path $AlertsDir -Filter '*.json' -File | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Host "Нет *.json в $AlertsDir — нечего применять." -ForegroundColor Yellow
    return
}

$created = 0; $updated = 0; $failed = 0

foreach ($file in $files) {
    Write-Host ""
    Write-Host "─── $($file.Name) ───" -ForegroundColor Cyan

    # -Encoding UTF8 обязателен: PS 5.1 на русской Windows читает файл в cp1251
    # и кириллица в именах правил превращается в мусор ещё до отправки.
    try {
        $rule = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Host "✗ Невалидный JSON: $($_.Exception.Message)" -ForegroundColor Red
        $failed++; continue
    }
    if (-not $rule.alert) {
        Write-Host "✗ Нет поля 'alert' (имя правила — обязательное)" -ForegroundColor Red
        $failed++; continue
    }

    # Канал обязателен на стороне SigNoz — предупреждаем понятно и заранее
    if ($channelNames.Count -gt 0) {
        $missing = @($rule.preferredChannels | Where-Object { $_ -and ($channelNames -notcontains $_) })
        if ($missing.Count -gt 0) {
            Write-Host "✗ Канал(ы) не найдены в SigNoz: $($missing -join ', ')" -ForegroundColor Red
            Write-Host "  Создай канал с таким именем (Settings → Notification Channels) — без канала SigNoz правило не примет." -ForegroundColor Yellow
            $failed++; continue
        }
    }

    $match = $existingRules | Where-Object { $_.alert -eq $rule.alert } | Select-Object -First 1

    if ($match) {
        Write-Host "↻ Update '$($rule.alert)' (id=$($match.id))" -ForegroundColor Yellow
        $r = Invoke-SignozApi 'PUT' "$SignozUrl/api/v1/rules/$($match.id)" $curlConfig $file.FullName
    }
    else {
        Write-Host "✓ Create '$($rule.alert)'" -ForegroundColor Green
        $r = Invoke-SignozApi 'POST' "$SignozUrl/api/v1/rules" $curlConfig $file.FullName
    }

    if ($r.Ok) {
        Write-Host "  ✓ OK (HTTP $($r.Code))" -ForegroundColor Green
        if ($match) { $updated++ } else { $created++ }
    }
    else {
        Write-Host "  ✗ HTTP $($r.Code): $($r.Payload)" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
Write-Host "─── Готово ───" -ForegroundColor Cyan
Write-Host "Created: $created, Updated: $updated, Failed: $failed" -ForegroundColor $(if ($failed) { 'Yellow' } else { 'Green' })
if ($failed) { exit 1 }
