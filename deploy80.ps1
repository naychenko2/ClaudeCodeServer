#Requires -Version 5.1
<#
    Быстрый деплой ClaudeCodeServer на порт 80 (хостовый, среда Production80).
    Пайплайн повторяет Dockerfile: собрать фронт -> опубликовать бэк -> фронт в wwwroot -> запуск.

    Переносимо: корень репы берётся от места скрипта ($PSScriptRoot), пути-параметры имеют дефолты.

    Использование (из любой папки):
      powershell -ExecutionPolicy Bypass -File deploy80.ps1                 # полный деплой + перезапуск
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipFrontend   # без пересборки фронта
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -NoRestart      # только собрать, без запуска
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipSandbox    # не трогать образ песочницы
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SandboxNoCache # пересобрать песочницу начисто (свежий claude CLI)
      ... -PublishDir 'D:\deploy\claude'   # другая папка публикации
#>
param(
    [switch]$SkipFrontend,
    [switch]$NoRestart,
    [switch]$SkipSandbox,      # не трогать образ песочницы claude-sandbox
    [switch]$SandboxNoCache,   # пересобрать образ песочницы начисто (свежий claude CLI из npm)
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
    Write-Host '[1/6] Сборка фронта (npm run build)...' -ForegroundColor Yellow
    Push-Location $frontendDir
    if (-not (Test-Path 'node_modules')) { npm ci }
    # build:quiet = vite build --logLevel warn: без простыни ассетов в логах раннера,
    # предупреждения и ошибки сборки остаются видны. Отдельный скрипт, а не `npm run build --
    # --logLevel warn`: npm парсит --logLevel как СВОЙ конфиг-ключ даже после `--` и съедает его.
    npm run build:quiet
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw "Сборка фронта упала (exit $code)" }
} else {
    Write-Host '[1/6] Фронт пропущен (-SkipFrontend)' -ForegroundColor DarkGray
}

# --- 2. Публикация бэка ---
Write-Host '[2/6] Публикация бэка (dotnet publish -c Release)...' -ForegroundColor Yellow
dotnet publish $csproj -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация бэка упала (exit $LASTEXITCODE)" }

# --- 3. Свежий фронт в wwwroot (рядом с exe) ---
Write-Host '[3/6] Копирование фронта в wwwroot...' -ForegroundColor Yellow
$wwwroot = Join-Path $PublishDir 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item "$wwwroot\*" -Recurse -Force -ErrorAction Stop }
else { New-Item -ItemType Directory -Force $wwwroot -ErrorAction Stop | Out-Null }
Copy-Item (Join-Path $frontendDir 'dist\*') $wwwroot -Recurse -Force -ErrorAction Stop

# --- 4. MCP-серверы (чистый Node, сборка не нужна) ---
Write-Host '[4/6] Копирование MCP-серверов...' -ForegroundColor Yellow
foreach ($srv in 'tasks-server', 'notes-server', 'memory-server', 'personas-server', 'workspace-server') {
    $mcpDst = Join-Path $PublishDir "mcp\$srv"
    New-Item -ItemType Directory -Force $mcpDst -ErrorAction Stop | Out-Null
    Copy-Item (Join-Path $repo "mcp\$srv\*") $mcpDst -Recurse -Force -ErrorAction Stop
}

# --- 5. Пересборка образа песочницы (синхронизация с версией хоста) ---
# Образ claude-sandbox несёт В СЕБЕ код MCP-серверов (/app/mcp), run-turn.sh, claude-defaults
# и сам claude CLI. Бэкенд на хосте свежий, но ходы container-юзеров и их MCP исполняются
# ВНУТРИ песочницы из образа — если его не пересобрать, доработки после последней сборки
# образа молча не работают в песочнице (рассинхрон). Поэтому пересборка — часть деплоя.
if ($SkipSandbox) {
    Write-Host '[5/6] Песочница пропущена (-SkipSandbox)' -ForegroundColor DarkGray
} else {
    Write-Host '[5/6] Пересборка образа песочницы claude-sandbox...' -ForegroundColor Yellow
    $dockerfile = Join-Path $repo 'backend\ClaudeHomeServer\Dockerfile'
    # Без --no-cache COPY-слои mcp/claude-defaults/run-turn.sh обновятся, но слой
    # `npm install -g claude` закэширован — для свежего CLI из npm нужен -SandboxNoCache.
    $buildArgs = @('build', '--target', 'sandbox', '-t', 'claude-sandbox', '-f', $dockerfile, $repo)
    if ($SandboxNoCache) { $buildArgs = @('build', '--no-cache') + $buildArgs[1..($buildArgs.Count - 1)] }
    docker @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Сборка образа песочницы упала (exit $LASTEXITCODE)" }

    # Пересоздать контейнер: имя из прод-конфига (Sandbox:ContainerName), дефолт cc-sandbox.
    # Бэкенд поднимет свежий контейнер лениво при первом ходе (EnsureRunningAsync по image ID),
    # но явное удаление гарантирует переход на новый образ сразу.
    $localCfg = Join-Path $PublishDir 'appsettings.Local.json'
    $containerName = 'cc-sandbox'
    if (Test-Path $localCfg) {
        try { $cn = (Get-Content $localCfg -Raw | ConvertFrom-Json).Sandbox.ContainerName; if ($cn) { $containerName = $cn } } catch {}
    }
    Write-Host "  Пересоздание контейнера $containerName..." -ForegroundColor DarkGray
    docker rm -f $containerName 2>$null | Out-Null
}

# --- 6. Перезапуск ---
if ($NoRestart) {
    Write-Host '[6/6] Запуск пропущен (-NoRestart)' -ForegroundColor DarkGray
} else {
    Write-Host '[6/6] Перезапуск сервера...' -ForegroundColor Yellow
    Get-Process ClaudeHomeServer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.exe') -WorkingDirectory $PublishDir
}

Write-Host ''
Write-Host 'Готово. Сервер запущен на порту 80.' -ForegroundColor Green
