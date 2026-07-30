<#
.SYNOPSIS
    Идемпотентный импортёр сохранённых представлений (Saved Views) SigNoz из JSON репозитория.

.DESCRIPTION
    Третий в семье к dashboards/apply.ps1 и alerts/apply-alerts.ps1: JSON в репе —
    source of truth, SigNoz — applied-копия.

    Представление = сохранённый запрос в Explorer (фильтр + разрезы + колонки) под именем.
    Дашборд отвечает «что происходит», представление — «дай ту самую выборку».

    Идемпотентность: GET /api/v1/explorer/views?sourcePage=<...> → матч по name →
    PUT если найдено, POST если новое.

.PARAMETER SignozUrl
    Базовый URL SigNoz. По умолчанию http://localhost:3301.

.PARAMETER Jwt
    Service Account key / PAT / JWT. По умолчанию $env:SIGNOZ_JWT (или файл кред).

.PARAMETER ViewsDir
    Папка с *.json. По умолчанию — та, где лежит скрипт.

.EXAMPLE
    .\apply-views.ps1

.NOTES
    Схема снята с живого SigNoz v0.134: compositeQuery в формате v5 (queries[].spec),
    старый builder.queryData API отвергает. Разбор — docs/observability.md, «Сохранённые представления».
#>
[CmdletBinding()]
param(
    [string]$SignozUrl = $(if ($env:SIGNOZ_URL) { $env:SIGNOZ_URL } else { 'http://localhost:3301' }),
    [string]$Jwt = $env:SIGNOZ_JWT,
    [string]$ViewsDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

# ── Креды из .signoz-credentials.ps1 (рядом или в родительской папке) ──────────
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

# SIGNOZ-API-KEY универсален; Bearer добавляем только настоящему JWT — при обоих сразу
# SigNoz предпочитает Authorization, и Service Account key падает с 401.
$isJwt = $Jwt.Split('.').Count -eq 3
$headers = @{ 'SIGNOZ-API-KEY' = $Jwt; 'Content-Type' = 'application/json; charset=utf-8' }
if ($isJwt) { $headers['Authorization'] = "Bearer $Jwt" }

# Заголовки для curl идут через stdin, чтобы ключ не светился в командной строке процесса
$curlConfig = @("header = `"SIGNOZ-API-KEY: $Jwt`"", 'header = "Content-Type: application/json; charset=utf-8"')
if ($isJwt) { $curlConfig += "header = `"Authorization: Bearer $Jwt`"" }
$curlConfig = $curlConfig -join "`n"

function Invoke-SignozApi([string]$Method, [string]$Uri, [string]$Config, [string]$File) {
    # HTTP-код проверяем явно: curl без -f отдаёт 0 и при 4xx, и протухший ключ
    # выглядел бы успешным импортом.
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

# Существующие представления кешируем по разделу: выдача БЕЗ ?sourcePage пуста —
# на этом легко решить, что ничего не создалось, и наплодить дублей.
$cache = @{}
function Get-Existing([string]$SourcePage) {
    if (-not $cache.ContainsKey($SourcePage)) {
        try {
            $r = Invoke-RestMethod -Uri "$SignozUrl/api/v1/explorer/views?sourcePage=$SourcePage" `
                -Headers $headers -TimeoutSec 10
            $cache[$SourcePage] = @($r.data)
        }
        catch { throw "GET /api/v1/explorer/views?sourcePage=$SourcePage не удался: $($_.Exception.Message)" }
    }
    return $cache[$SourcePage]
}

$files = @(Get-ChildItem -Path $ViewsDir -Filter '*.json' -File | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Host "Нет *.json в $ViewsDir — нечего применять." -ForegroundColor Yellow
    return
}

$created = 0; $updated = 0; $failed = 0

foreach ($file in $files) {
    Write-Host ""
    Write-Host "─── $($file.Name) ───" -ForegroundColor Cyan

    # -Encoding UTF8 обязателен: PS 5.1 на русской Windows читает файл в cp1251,
    # и кириллица в именах превращается в мусор ещё до отправки.
    try {
        $view = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Host "✗ Невалидный JSON: $($_.Exception.Message)" -ForegroundColor Red
        $failed++; continue
    }
    if (-not $view.name -or -not $view.sourcePage) {
        Write-Host "✗ Нужны поля 'name' и 'sourcePage'" -ForegroundColor Red
        $failed++; continue
    }

    $match = Get-Existing $view.sourcePage | Where-Object { $_.name -eq $view.name } | Select-Object -First 1

    # Обновление — это УДАЛИТЬ + СОЗДАТЬ, а не PUT.
    #
    # PUT /api/v1/explorer/views/{id} в SigNoz v0.134 отвечает 200, но сохраняет запрос
    # испорченным: последующий GET списка падает целиком с
    # «error in unmarshalling explorer query data: invalid character '\'».
    # То есть ломается не одно представление, а вся выдача раздела — чинится только
    # удалением битой записи по id. Проверено: POST → GET ок, тот же файл через
    # PUT → GET сломан.
    if ($match) {
        Write-Host "↻ Replace '$($view.name)' (id=$($match.id))" -ForegroundColor Yellow
        $del = $curlConfig | & curl.exe -sS -X DELETE "$SignozUrl/api/v1/explorer/views/$($match.id)" `
            --config - -w "`n%{http_code}" 2>&1
        $delCode = (($del | Out-String).TrimEnd("`r", "`n") -split "`r?`n")[-1]
        if ($delCode -notmatch '^2\d\d$') {
            Write-Host "  ✗ Не удалось удалить прежнее (HTTP $delCode)" -ForegroundColor Red
            $failed++; continue
        }
        $r = Invoke-SignozApi 'POST' "$SignozUrl/api/v1/explorer/views" $curlConfig $file.FullName
    }
    else {
        Write-Host "✓ Create '$($view.name)' [$($view.sourcePage)]" -ForegroundColor Green
        $r = Invoke-SignozApi 'POST' "$SignozUrl/api/v1/explorer/views" $curlConfig $file.FullName
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
