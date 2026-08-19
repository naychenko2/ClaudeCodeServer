<#
.SYNOPSIS
    Идемпотентный импортёр SigNoz-дашбордов из JSON-файлов в репозитории.

.DESCRIPTION
    Применяет все *.json из текущей папки (или из -DashboardsDir) в SigNoz через
    /api/v1/dashboards API. Source of truth — JSON в репозитории, SigNoz = applied-копия.

    Auth — JWT Bearer token. Токен можно:
      • передать готовым через -Jwt или $env:SIGNOZ_JWT
      • получить автоматически через -Email/-Password или $env:SIGNOZ_EMAIL/ SIGNOZ_PASSWORD
        (логин через POST /api/v1/login, обычный email + пароль SigNoz UI)

    Идемпотентность: GET /api/v1/dashboards → матч по data.title → PUT если найден,
    POST если нового. Re-run безопасен, дашборды не дублируются.

.PARAMETER SignozUrl
    Базовый URL SigNoz ВМЕСТЕ с base-path. По умолчанию http://localhost:3301/telemetry-proxy:
    с v0.134 SigNoz поднимает весь HTTP-сервер под префиксом из SIGNOZ_GLOBAL_EXTERNAL__URL,
    и URL без него отвечает 404 на всё, кроме /api/v1/health.

.PARAMETER Jwt
    Готовый JWT access-токен. Если не задан — берётся из $env:SIGNOZ_JWT.
    Если отсутствует — нужен -Email/-Password для логина.

.PARAMETER Email
    Email пользователя SigNoz. Альтернатива -Jwt. По умолчанию $env:SIGNOZ_EMAIL.

.PARAMETER Password
    Пароль пользователя SigNoz. По умолчанию $env:SIGNOZ_PASSWORD.

.PARAMETER DashboardsDir
    Папка с JSON-файлами. По умолчанию — та, где лежит apply.ps1.

.EXAMPLE
    # С готовым JWT
    .\apply.ps1 -Jwt "eyJhbGciOi..."

.EXAMPLE
    # Через логин/пароль
    .\apply.ps1 -Email "admin@example.com" -Password "secret"

.EXAMPLE
    # Через env-переменные
    $env:SIGNOZ_EMAIL = "admin@example.com"
    $env:SIGNOZ_PASSWORD = "secret"
    .\apply.ps1

.NOTES
    SigNoz v0.71 dashboard API — v1 (snake_case, layout+widgets, НЕ Perses v2).
    Документация схемы: docs/observability/dashboards.md.
#>
[CmdletBinding(DefaultParameterSetName = 'Jwt')]
param(
    [string]$SignozUrl = $(if ($env:SIGNOZ_URL) { $env:SIGNOZ_URL } else { 'http://localhost:3301/telemetry-proxy' }),

    [Parameter(ParameterSetName = 'Jwt')]
    [string]$Jwt = $env:SIGNOZ_JWT,

    [Parameter(ParameterSetName = 'Login')]
    [string]$Email = $env:SIGNOZ_EMAIL,

    [Parameter(ParameterSetName = 'Login')]
    [string]$Password = $env:SIGNOZ_PASSWORD,

    [string]$DashboardsDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

# ── 0. Автозагрузка кред из .signoz-credentials.ps1 (если есть, рядом с dashboards/) ──
# Файл в .gitignore, можно положить рядом с apply.ps1 или в родителе (docker/observability/).
# Шаблон: docker/observability/.signoz-credentials.example.ps1.
if (-not $Jwt -and -not $Email) {
    $credPaths = @(
        (Join-Path $PSScriptRoot '.signoz-credentials.ps1'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) '.signoz-credentials.ps1')
    )
    foreach ($p in $credPaths) {
        if (Test-Path $p) {
            . $p
            # env-переменные из cred-файла пополняют defaults
            if (-not $SignozUrl -and $env:SIGNOZ_URL) { $SignozUrl = $env:SIGNOZ_URL }
            if (-not $Jwt -and $env:SIGNOZ_JWT)        { $Jwt = $env:SIGNOZ_JWT }
            if (-not $Email -and $env:SIGNOZ_EMAIL)    { $Email = $env:SIGNOZ_EMAIL }
            if (-not $Password -and $env:SIGNOZ_PASSWORD) { $Password = $env:SIGNOZ_PASSWORD }
            break
        }
    }
}

# ── 1. Получаем JWT ────────────────────────────────────────────────────────────
if (-not $Jwt) {
    if (-not $Email -or -not $Password) {
        throw "Нужен либо -Jwt, либо -Email/-Password (или env SIGNOZ_JWT / SIGNOZ_EMAIL + SIGNOZ_PASSWORD)"
    }

    Write-Host "→ Логин в SigNoz как $Email..." -ForegroundColor Cyan
    $loginBody = @{ email = $Email; password = $Password } | ConvertTo-Json
    try {
        $loginResp = Invoke-RestMethod -Uri "$SignozUrl/api/v1/login" `
            -Method Post -Body $loginBody -ContentType 'application/json' -TimeoutSec 10
        # SigNoz возвращает { access_jwt, refresh_jwt, ... } — точное поле зависит от версии
        $Jwt = if ($loginResp.accessJwt) { $loginResp.accessJwt }
               elseif ($loginResp.access_jwt) { $loginResp.access_jwt }
               elseif ($loginResp.data.accessJwt) { $loginResp.data.accessJwt }
               elseif ($loginResp.data.access_jwt) { $loginResp.data.access_jwt }
               else { throw "В ответе /api/v1/login нет поля access_jwt. Полный ответ: $($loginResp | ConvertTo-Json -Depth 5 -Compress)" }
        Write-Host "✓ JWT получен (длина: $($Jwt.Length))" -ForegroundColor Green
    }
    catch {
        throw "Логин не удался: $($_.Exception.Message)"
    }
}

# Headers: SigNoz v0.134+ использует `SIGNOZ-API-KEY` для Service Account keys и PAT.
# Старый `Authorization: Bearer <jwt>` всё ещё работает для JWT из /api/v1/login, но
# при наличии обоих SigNoz приоритизирует Authorization — Service Account key падает с 401.
# Решение: всегда слать SIGNOZ-API-KEY (универсальный), а Bearer добавлять только
# если токен выглядит как JWT (содержит две точки — header.payload.signature).
$isJwt = $Jwt -and $Jwt.Split('.').Count -eq 3
$headers = @{
    'SIGNOZ-API-KEY' = $Jwt
    'Content-Type'   = 'application/json; charset=utf-8'
}
if ($isJwt) { $headers['Authorization'] = "Bearer $Jwt" }

# Те же заголовки для curl.exe — но через stdin, а не аргументами. Командная строка
# процесса на Windows читается любым процессом того же пользователя (Get-CimInstance
# Win32_Process, диспетчер задач с колонкой «Командная строка»), поэтому
# `-H "SIGNOZ-API-KEY: ..."` показывал ключ всем желающим на время запроса.
# `curl --config -` читает опции со стандартного ввода: ключ не попадает ни в аргументы,
# ни на диск (временный файл пришлось бы ещё и гарантированно удалять).
# Тело запроса идёт отдельно, через --data-binary @файл, так что stdin свободен.
$curlConfig = @("header = `"SIGNOZ-API-KEY: $Jwt`"", 'header = "Content-Type: application/json; charset=utf-8"')
if ($isJwt) { $curlConfig += "header = `"Authorization: Bearer $Jwt`"" }
$curlConfig = $curlConfig -join "`n"

# PowerShell 5.1 по умолчанию конвертирует строку-Body в ISO-8859-1 → кириллица
# превращается в '?' на стороне SigNoz. Явно кодируем в UTF-8 байты.
$Utf8 = [System.Text.Encoding]::UTF8

# ── 2. GET существующих дашбордов (для идемпотентности) ────────────────────────
Write-Host "→ Получение списка существующих дашбордов..." -ForegroundColor Cyan
try {
    $existing = Invoke-RestMethod -Uri "$SignozUrl/api/v1/dashboards" -Headers $headers -TimeoutSec 10
}
catch {
    throw "GET /api/v1/dashboards не удался: $($_.Exception.Message). Проверь JWT и URL. Если это 404 — URL нужен С base-path (по умолчанию /telemetry-proxy): с v0.134 SigNoz поднимает под ним ВЕСЬ API, в корне живёт только /api/v1/health."
}

# SigNoz v0.71 возвращает { status, data: [...] } или массив напрямую
$existingList = if ($existing.data) { $existing.data } else { @($existing) }
Write-Host "✓ Найдено дашбордов: $($existingList.Count)" -ForegroundColor Green

# ── 3. Применяем каждый JSON ───────────────────────────────────────────────────
$dashboardsFiles = @(Get-ChildItem -Path $DashboardsDir -Filter '*.json' -File)
if ($dashboardsFiles.Count -eq 0) {
    Write-Host "Нет *.json в папке $DashboardsDir — нечего применять." -ForegroundColor Yellow
    return
}

$created = 0; $updated = 0; $failed = 0

foreach ($file in $dashboardsFiles) {
    Write-Host ""
    Write-Host "─── $($file.Name) ───" -ForegroundColor Cyan

    # Парсим JSON-определение. -Encoding UTF8 обязателен: PS 5.1 на русской Windows
    # по умолчанию читает в cp1251, кириллица превращается в моши на этапе чтения файла.
    try {
        $definition = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Host "✗ Невалидный JSON: $($_.Exception.Message)" -ForegroundColor Red
        $failed++; continue
    }
    if (-not $definition.title) {
        Write-Host "✗ В JSON нет поля 'title' (обязательное)" -ForegroundColor Red
        $failed++; continue
    }

    # Матч по title (массив data[*].data.title)
    $match = $existingList | Where-Object {
        ($_.data.title -eq $definition.title) -or ($_.title -eq $definition.title)
    } | Select-Object -First 1

    # SigNoz v0.134: идентификатор в поле `id` (не `uuid`, как в v0.71).
    $id = if ($match) { if ($match.uuid) { $match.uuid } else { $match.id } } else { $null }

    # Body через curl.exe: PowerShell 5.1 Invoke-RestMethod в ISO-8859-1 ломает
    # кириллицу на отправке (даже с UTF-8 byte[]), а curl.exe шлёт файл как есть.
    $bodyFile = $file.FullName

    # HTTP-код печатаем последней строкой (-w) и проверяем ЯВНО. Только на $LASTEXITCODE
    # полагаться нельзя: curl без -f отдаёт 0 и при 401/403/404/500, поэтому протухший
    # ключ выглядел как «✓ OK» — скрипт рапортовал успех, а в SigNoz ничего не заливалось.
    # Заголовки (с ключом) приходят в curl по stdin через --config -, см. выше.
    function Invoke-SignozApi([string]$Method, [string]$Uri, [string]$Config, [string]$File) {
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

    if ($match) {
        Write-Host "↻ Update '$($definition.title)' (id=$id)" -ForegroundColor Yellow
        $r = Invoke-SignozApi 'PUT' "$SignozUrl/api/v1/dashboards/$id" $curlConfig $bodyFile
        if ($r.Ok) {
            Write-Host "  ✓ OK (HTTP $($r.Code))" -ForegroundColor Green
            $updated++
        } else {
            Write-Host "  ✗ HTTP $($r.Code): $($r.Payload)" -ForegroundColor Red
            $failed++
        }
    }
    else {
        Write-Host "✓ Create '$($definition.title)'" -ForegroundColor Green
        $r = Invoke-SignozApi 'POST' "$SignozUrl/api/v1/dashboards" $curlConfig $bodyFile
        if ($r.Ok) {
            $newId = '?'
            try {
                $respObj = $r.Payload | ConvertFrom-Json
                if ($respObj.data) {
                    if ($respObj.data.id) { $newId = $respObj.data.id }
                    elseif ($respObj.data.uuid) { $newId = $respObj.data.uuid }
                } elseif ($respObj.id) { $newId = $respObj.id }
                elseif ($respObj.uuid) { $newId = $respObj.uuid }
            } catch { $newId = '?' }
            Write-Host "  ✓ OK (HTTP $($r.Code), id=$newId)" -ForegroundColor Green
            $created++
        } else {
            Write-Host "  ✗ HTTP $($r.Code): $($r.Payload)" -ForegroundColor Red
            $failed++
        }
    }
}

# ── 4. Итог ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "─── Готово ───" -ForegroundColor Cyan
Write-Host "Created: $created, Updated: $updated, Failed: $failed" -ForegroundColor $(if ($failed) { 'Yellow' } else { 'Green' })
if ($failed) { exit 1 }

