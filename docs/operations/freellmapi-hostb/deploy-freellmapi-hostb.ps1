<#
.SYNOPSIS
  Деплой FreeLLMAPI на Host B (Windows).
.DESCRIPTION
  Скрипт выполняется НА HOST B (192.168.7.208) после того, как оператор
  перенёс сюда файлы из C:\Temp\freellmapi-hostb\ на Host A.
  Шаги:
    1. docker load - загрузить образ freellmapi:prod
    2. docker volume create - создать том
    3. распаковка freellmapi-data.tar.gz в том (через вспомогательный alpine)
    4. docker compose up -d
    5. проверка /api/ping
.NOTES
  Запускать в PowerShell от имени администратора (нужен доступ к Docker).
  PS 5.1 не любит вложенные if/else внутри catch - обходимся через -ErrorAction.
#>

[CmdletBinding()]
param(
    [string]$BundleDir = "C:\Temp\freellmapi-hostb",
    [string]$DeployDir = "C:\freellmapi-hostb"
)

$ErrorActionPreference = 'Stop'

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

function Fail
{
    param([string]$msg)
    Write-Host "    FAIL: $msg" -ForegroundColor Red
    throw $msg
}

Step "Проверка окружения"
if (-not (Test-Path "$BundleDir\freellmapi-image.tar")) { Fail "Не найден $BundleDir\freellmapi-image.tar" }
if (-not (Test-Path "$BundleDir\freellmapi-data.tar.gz")) { Fail "Не найден $BundleDir\freellmapi-data.tar.gz" }

$dockerOut = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Docker недоступен (код $LASTEXITCODE). Запустите скрипт от администратора с работающим Docker Desktop"
}
Ok "Docker доступен, файлы на месте"

Step "Подготовка каталога развёртывания"
if (-not (Test-Path $DeployDir)) {
    New-Item -ItemType Directory -Path $DeployDir | Out-Null
}
Copy-Item -Force "$BundleDir\freellmapi-image.tar" "$DeployDir\"
Copy-Item -Force "$BundleDir\freellmapi-data.tar.gz" "$DeployDir\"
Copy-Item -Force "$PSScriptRoot\docker-compose.yml" "$DeployDir\"
Copy-Item -Force "$PSScriptRoot\.env" "$DeployDir\"
Ok "Каталог $DeployDir подготовлен"

Step "Загрузка образа"
docker load -i "$DeployDir\freellmapi-image.tar"
Ok "Образ freellmapi:prod загружен"

Step "Восстановление volume freellmapi-data"
$volExists = $false
docker volume ls --format '{{.Name}}' | ForEach-Object {
    if ($_ -eq 'freellmapi-data') { $volExists = $true }
}
if (-not $volExists) {
    docker volume create freellmapi-data | Out-Null
}
docker run --rm -v freellmapi-data:/dst -v "$DeployDir":/src alpine sh -c "rm -rf /dst/* /dst/.[!.]* 2>/dev/null; tar xzf /src/freellmapi-data.tar.gz -C /dst"
Ok "Volume freellmapi-data восстановлен из дампа"

Step "Запуск контейнера"
Set-Location $DeployDir
docker compose up -d
Ok "Контейнер запущен"

Step "Ожидание healthcheck (до 30с)"
$ok = $false
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 3
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:3001/api/ping" -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
    if ($null -ne $resp -and $resp.StatusCode -eq 200) {
        $ok = $true
        break
    }
}
if (-not $ok) { Fail "FreeLLM не ответил /api/ping за 30 секунд - проверьте docker logs freellmapi" }
Ok "Healthcheck OK"

Step "Проверка unified-ключа (Bearer)"
$envFile = Get-Content "$DeployDir\.env" | Where-Object { $_ -match '^ENCRYPTION_KEY=' }
$secret = ($envFile -split '=', 2)[1]
$headers = @{ Authorization = "Bearer $secret" }
$resp = Invoke-WebRequest -Uri "http://127.0.0.1:3001/api/keys" -Headers $headers -Method Get -TimeoutSec 10 -ErrorAction SilentlyContinue
if ($null -ne $resp) {
    $keys = $resp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
    if ($null -ne $keys) {
        Ok ("Unified-ключ принят сервером, в /api/keys " + @($keys).Count + " записей")
    } else {
        Warn "Ключ принят, но ответ не распознан как JSON - это нормально, если ключей ещё нет"
    }
} else {
    $statusCode = 0
    if ($Error.Count -gt 0 -and $null -ne $Error[0].Exception.Response) {
        $statusCode = $Error[0].Exception.Response.StatusCode.value__
    }
    if ($statusCode -eq 401) {
        Fail "Unified-ключ из .env не принят сервером (401). Проверьте ENCRYPTION_KEY - возможно, пришёл битый дамп volume."
    } else {
        Warn "Не удалось проверить ключ (HTTP $statusCode). Контейнер работает, но /api/keys не ответил."
    }
}

Write-Host ""
Write-Host "==> Готово." -ForegroundColor Green
Write-Host "    Контейнер: docker ps | Select-String freellmapi"
Write-Host "    Логи:      docker logs -f freellmapi"
Write-Host "    Сеть:      netstat -ano | Select-String ':3001'"
Write-Host "    Firewall:  запустите firewall-freellmapi.ps1, чтобы ограничить 3001 только Host A"
