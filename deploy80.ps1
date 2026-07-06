#Requires -Version 5.1
<#
    Быстрый деплой ClaudeCodeServer на порт 80 (хостовый, среда Production80).
    Пайплайн повторяет Dockerfile: собрать фронт -> опубликовать бэк -> фронт в wwwroot -> запуск.

    Переносимо: корень репы берётся от места скрипта ($PSScriptRoot), пути-параметры имеют дефолты.

    Использование (из любой папки):
      powershell -ExecutionPolicy Bypass -File deploy80.ps1                 # полный деплой + перезапуск
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipFrontend   # без пересборки фронта
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -NoRestart      # только собрать, без запуска
      ... -PublishDir 'D:\deploy\claude'   # другая папка публикации
#>
param(
    [switch]$SkipFrontend,
    [switch]$NoRestart,
    [string]$PublishDir  = 'C:\deploy\claude',
    [string]$Environment = 'Production80'
)
# НЕ 'Stop' глобально: npm/dotnet пишут предупреждения в stderr, а на Windows PowerShell это
# со 'Stop' ложно роняет скрипт (npm.ps1 наследует preference). Нативные проверяем по
# $LASTEXITCODE, критичным командлетам даём -ErrorAction Stop.
$ErrorActionPreference = 'Continue'

# --- Пути (корень репы = папка этого скрипта, без хардкода) ---
$repo        = $PSScriptRoot
$frontendDir = Join-Path $repo 'frontend'
$csproj      = Join-Path $repo 'backend\ClaudeHomeServer\ClaudeHomeServer.csproj'
$env:ASPNETCORE_ENVIRONMENT = $Environment

Write-Host "=== Деплой ClaudeCodeServer -> $PublishDir (env $Environment) ===" -ForegroundColor Cyan

# --- 1. Сборка фронта ---
if (-not $SkipFrontend) {
    Write-Host '[1/5] Сборка фронта (npm run build)...' -ForegroundColor Yellow
    Push-Location $frontendDir
    if (-not (Test-Path 'node_modules')) { npm ci }
    npm run build
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw "Сборка фронта упала (exit $code)" }
} else {
    Write-Host '[1/5] Фронт пропущен (-SkipFrontend)' -ForegroundColor DarkGray
}

# --- 2. Публикация бэка ---
Write-Host '[2/5] Публикация бэка (dotnet publish -c Release)...' -ForegroundColor Yellow
dotnet publish $csproj -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация бэка упала (exit $LASTEXITCODE)" }

# --- 3. Свежий фронт в wwwroot (рядом с exe) ---
Write-Host '[3/5] Копирование фронта в wwwroot...' -ForegroundColor Yellow
$wwwroot = Join-Path $PublishDir 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item "$wwwroot\*" -Recurse -Force -ErrorAction Stop }
else { New-Item -ItemType Directory -Force $wwwroot -ErrorAction Stop | Out-Null }
Copy-Item (Join-Path $frontendDir 'dist\*') $wwwroot -Recurse -Force -ErrorAction Stop

# --- 4. MCP-сервер задач (чистый Node, сборка не нужна) ---
Write-Host '[4/5] Копирование MCP tasks-server...' -ForegroundColor Yellow
$mcpDst = Join-Path $PublishDir 'mcp\tasks-server'
New-Item -ItemType Directory -Force $mcpDst -ErrorAction Stop | Out-Null
Copy-Item (Join-Path $repo 'mcp\tasks-server\*') $mcpDst -Recurse -Force -ErrorAction Stop

# --- 5. Перезапуск ---
if ($NoRestart) {
    Write-Host '[5/5] Запуск пропущен (-NoRestart)' -ForegroundColor DarkGray
} else {
    Write-Host '[5/5] Перезапуск сервера...' -ForegroundColor Yellow
    Get-Process ClaudeHomeServer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.exe') -WorkingDirectory $PublishDir
}

Write-Host ''
Write-Host 'Готово. Сервер запущен на порту 80.' -ForegroundColor Green
