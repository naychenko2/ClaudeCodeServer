<#
.SYNOPSIS
  Применить патчи appsettings.Local.json дева и прода для FreeLLM на Host B.
.DESCRIPTION
  Выполнять на Host A ПОСЛЕ успешного деплоя FreeLLM на Host B.
  Делает резервную копию каждого файла (appsettings.Local.json.bak-<timestamp>),
  патчит секцию LlmProviders.freellmapi (или добавляет её), валидирует JSON через
  ConvertFrom-Json (сломается — откатываемся).

  Tracked appsettings.json НЕ трогает.

  По умолчанию — DRY RUN (только отчёт, без правок). Чтобы применить:
    .\apply-ccs-config.ps1 -Apply
.NOTES
  Запускать в обычной PowerShell — админ не нужен.
#>

[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$DevLocalJson = "C:\Sources\ClaudeCodeServer\backend\ClaudeHomeServer\appsettings.Local.json",
    [string]$ProdLocalJson = "C:\ClaudeServer\prod\appsettings.Local.json",
    [string]$HostB = "192.168.7.208",
    [int]$Port = 3001,
    [string]$ApiKey = $env:FREELLM_BEARER_KEY
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Host "FAIL: Bearer-ключ FreeLLM не задан." -ForegroundColor Red
    Write-Host "Задайте через окружение и перезапустите:" -ForegroundColor Yellow
    Write-Host '  $env:FREELLM_BEARER_KEY = "freellmapi-..."' -ForegroundColor Yellow
    Write-Host "или параметром: .\apply-ccs-config.ps1 -ApiKey 'freellmapi-...'" -ForegroundColor Yellow
    exit 1
}

function Step
{
    param([string]$msg)
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Ok
{
    param([string]$msg)
    Write-Host "    OK: $msg" -ForegroundColor Green
}

function Warn
{
    param([string]$msg)
    Write-Host "    WARN: $msg" -ForegroundColor Yellow
}

$anthropicUrl = "http://${HostB}:${Port}"
$apiBaseUrl   = "${anthropicUrl}/v1"

function Patch-LocalJson
{
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path $Path)) {
        Write-Host "    SKIP ($Label): $Path не найден" -ForegroundColor Yellow
        return
    }

    Step "Патчим $Label -> $Path"
    $json = Get-Content $Path -Raw -Encoding UTF8
    $obj = $json | ConvertFrom-Json

    if (-not $obj.LlmProviders) {
        $obj | Add-Member -NotePropertyName LlmProviders -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $providers = $obj.LlmProviders
    if (-not ($providers.PSObject.Properties.Name -contains 'freellmapi')) {
        $providers | Add-Member -NotePropertyName freellmapi -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $free = $providers.freellmapi
    if ($free.PSObject.Properties.Name -contains 'AnthropicBaseUrl') {
        $free.AnthropicBaseUrl = $anthropicUrl
    } else {
        $free | Add-Member -NotePropertyName AnthropicBaseUrl -NotePropertyValue $anthropicUrl -Force
    }
    if ($free.PSObject.Properties.Name -contains 'ApiBaseUrl') {
        $free.ApiBaseUrl = $apiBaseUrl
    } else {
        $free | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $apiBaseUrl -Force
    }
    if ($free.PSObject.Properties.Name -contains 'ApiKey') {
        $free.ApiKey = $ApiKey
    } else {
        $free | Add-Member -NotePropertyName ApiKey -NotePropertyValue $ApiKey -Force
    }

    $newJson = $obj | ConvertTo-Json -Depth 20

    if (-not $Apply) {
        Write-Host "    DRY-RUN: показал бы патч. Запусти с -Apply чтобы применить." -ForegroundColor Yellow
        Write-Host "    Будет: AnthropicBaseUrl=$anthropicUrl, ApiBaseUrl=$apiBaseUrl, ApiKey=<тот же>" -ForegroundColor Yellow
        return
    }

    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $bak = "$Path.bak-$ts"
    Copy-Item $Path $bak -Force
    Ok "Бэкап: $bak"

    Set-Content -Path $Path -Value $newJson -Encoding UTF8 -NoNewline
    Ok "Записан $Path"

    # Проверим, что итоговый файл всё ещё валидный JSON (и вернём наружу через $LASTEXITCODE)
    try {
        $recheck = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $recheck.LlmProviders.freellmapi) {
            throw "LlmProviders.freellmapi пропал после перезаписи"
        }
        Ok "JSON валиден, секция freellmapi на месте"
    }
    catch {
        Warn "ПОСЛЕ перезаписи JSON не валиден: $($_.Exception.Message). Откат из $bak"
        Copy-Item $bak $Path -Force
        throw
    }
}

Patch-LocalJson -Path $DevLocalJson  -Label "dev"
Patch-LocalJson -Path $ProdLocalJson -Label "prod"

Step "Готово"
if (-not $Apply) {
    Write-Host "    Режим DRY-RUN. Чтобы применить:" -ForegroundColor Yellow
    Write-Host "      .\apply-ccs-config.ps1 -Apply" -ForegroundColor Yellow
}
