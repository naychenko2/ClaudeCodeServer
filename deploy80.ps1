#Requires -Version 5.1
<#
    Быстрый деплой ClaudeCodeServer на порт 80 (хостовый). По умолчанию сервер запускается
    под трей-супервизором (ClaudeHomeServer.Tray.exe): без консольного окна, с иконкой в трее
    (открыть в браузере, перезапустить, статистика, логи, выход) и авто-рестартом при падении.
    Пайплайн повторяет Dockerfile: стоп -> фронт -> публикация бэка/трея -> фронт в wwwroot ->
    MCP -> песочница -> автозапуск -> старт.

    Переносимо: корень репы берётся от места скрипта ($PSScriptRoot), пути-параметры имеют дефолты.

    Использование (из любой папки):
      powershell -ExecutionPolicy Bypass -File deploy80.ps1                 # полный деплой + перезапуск (трей)
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipFrontend   # без пересборки фронта
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -NoRestart      # только собрать, без запуска
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipSandbox    # не трогать образ песочницы
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SandboxNoCache # пересобрать песочницу начисто
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -Console        # старый режим: сервер прямым процессом (с консолью)
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -NoAutostart    # не трогать ярлык автозапуска
      ... -PublishDir 'D:\deploy\claude' -AppUrl 'https://naychenko.me' -Port 80
#>
param(
    [switch]$SkipFrontend,
    [switch]$NoRestart,
    [switch]$SkipSandbox,      # не трогать образ песочницы claude-sandbox
    [switch]$SandboxNoCache,   # пересобрать образ песочницы начисто (свежий claude CLI из npm)
    [switch]$Console,          # запускать сервер прямым процессом (старый режим), без трея
    [switch]$NoAutostart,      # не создавать/обновлять ярлык автозапуска трея
    [string]$PublishDir  = 'C:\deploy\claude',
    [string]$Environment = 'Production80',
    [string]$AppUrl      = 'https://naychenko.me',
    [int]$Port           = 80
)
# НЕ 'Stop' глобально: npm/dotnet пишут предупреждения в stderr, а на Windows PowerShell это
# со 'Stop' ложно роняет скрипт (npm.ps1 наследует preference). Нативные проверяем по
# $LASTEXITCODE, критичным командлетам даём -ErrorAction Stop.
$ErrorActionPreference = 'Continue'

# --- Пути (корень репы = папка этого скрипта, без хардкода) ---
$repo        = $PSScriptRoot
$frontendDir = Join-Path $repo 'frontend'
$csproj      = Join-Path $repo 'backend\ClaudeHomeServer\ClaudeHomeServer.csproj'
$trayproj    = Join-Path $repo 'backend\ClaudeHomeServer.Tray\ClaudeHomeServer.Tray.csproj'
$env:ASPNETCORE_ENVIRONMENT = $Environment

Write-Host "=== Деплой ClaudeCodeServer -> $PublishDir (env $Environment) ===" -ForegroundColor Cyan

# --- 1. Остановка запущенных процессов (снять локи файлов ДО публикации) ---
# Трей глушим ПЕРВЫМ, чтобы его супервизор не перезапустил сервер, пока мы его убиваем.
Write-Host '[1/8] Остановка запущенных процессов...' -ForegroundColor Yellow
Get-Process ClaudeHomeServer.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Get-Process ClaudeHomeServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

# --- 2. Сборка фронта ---
if (-not $SkipFrontend) {
    Write-Host '[2/8] Сборка фронта (npm run build)...' -ForegroundColor Yellow
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
    Write-Host '[2/8] Фронт пропущен (-SkipFrontend)' -ForegroundColor DarkGray
}

# --- 3. Публикация бэка ---
Write-Host '[3/8] Публикация бэка (dotnet publish -c Release)...' -ForegroundColor Yellow
dotnet publish $csproj -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация бэка упала (exit $LASTEXITCODE)" }

# --- 4. Публикация трей-супервизора (рядом с сервером) ---
if (-not $Console) {
    Write-Host '[4/8] Публикация трей-супервизора...' -ForegroundColor Yellow
    dotnet publish $trayproj -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "Публикация трея упала (exit $LASTEXITCODE)" }
    # Конфиг трея: окружение дочернего сервера и URL «Открыть в браузере» — из параметров деплоя.
    $trayCfg = [ordered]@{ ServerExe = 'ClaudeHomeServer.exe'; Environment = $Environment; Url = $AppUrl; Port = $Port }
    ($trayCfg | ConvertTo-Json) | Set-Content -Path (Join-Path $PublishDir 'tray.json') -Encoding UTF8
} else {
    Write-Host '[4/8] Трей пропущен (-Console)' -ForegroundColor DarkGray
}

# --- 5. Свежий фронт в wwwroot (рядом с exe) ---
Write-Host '[5/8] Копирование фронта в wwwroot...' -ForegroundColor Yellow
$wwwroot = Join-Path $PublishDir 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item "$wwwroot\*" -Recurse -Force -ErrorAction Stop }
else { New-Item -ItemType Directory -Force $wwwroot -ErrorAction Stop | Out-Null }
Copy-Item (Join-Path $frontendDir 'dist\*') $wwwroot -Recurse -Force -ErrorAction Stop

# --- 6. MCP-серверы (чистый Node, сборка не нужна) ---
Write-Host '[6/8] Копирование MCP-серверов...' -ForegroundColor Yellow
foreach ($srv in 'tasks-server', 'notes-server', 'memory-server', 'personas-server', 'workspace-server') {
    $mcpDst = Join-Path $PublishDir "mcp\$srv"
    New-Item -ItemType Directory -Force $mcpDst -ErrorAction Stop | Out-Null
    Copy-Item (Join-Path $repo "mcp\$srv\*") $mcpDst -Recurse -Force -ErrorAction Stop
}

# --- 7. Пересборка образа песочницы (синхронизация с версией хоста) ---
# Образ claude-sandbox несёт В СЕБЕ код MCP-серверов (/app/mcp), run-turn.sh, claude-defaults
# и сам claude CLI. Бэкенд на хосте свежий, но ходы container-юзеров и их MCP исполняются
# ВНУТРИ песочницы из образа — если его не пересобрать, доработки после последней сборки
# образа молча не работают в песочнице (рассинхрон). Поэтому пересборка — часть деплоя.
if ($SkipSandbox) {
    Write-Host '[7/8] Песочница пропущена (-SkipSandbox)' -ForegroundColor DarkGray
} else {
    Write-Host '[7/8] Пересборка образа песочницы claude-sandbox...' -ForegroundColor Yellow
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

# --- 8. Автозапуск (ярлык трея в «Автозагрузке» текущего пользователя) + старт ---
if (-not $Console -and -not $NoAutostart) {
    try {
        $startup = [Environment]::GetFolderPath('Startup')
        # Снести старый ярлык (переименование на «AI Home»)
        Remove-Item (Join-Path $startup 'ClaudeHomeServer.lnk') -Force -ErrorAction SilentlyContinue
        $lnk = Join-Path $startup 'AI Home.lnk'
        $trayExe = Join-Path $PublishDir 'ClaudeHomeServer.Tray.exe'
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnk)
        $sc.TargetPath = $trayExe
        $sc.WorkingDirectory = $PublishDir
        $sc.Description = 'AI Home'
        $sc.IconLocation = "$trayExe,0"
        $sc.Save()
        Write-Host "  Автозапуск: $lnk" -ForegroundColor DarkGray
    } catch {
        Write-Host "  Не удалось создать ярлык автозапуска: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

if ($NoRestart) {
    Write-Host '[8/8] Запуск пропущен (-NoRestart)' -ForegroundColor DarkGray
} elseif ($Console) {
    Write-Host '[8/8] Запуск сервера (консольный режим)...' -ForegroundColor Yellow
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.exe') -WorkingDirectory $PublishDir
} else {
    Write-Host '[8/8] Запуск трей-супервизора (он поднимет сервер)...' -ForegroundColor Yellow
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.Tray.exe') -WorkingDirectory $PublishDir
}

Write-Host ''
Write-Host 'Готово. Сервер запущен на порту 80.' -ForegroundColor Green
