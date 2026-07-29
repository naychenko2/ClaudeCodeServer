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
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -IgnoreRunner   # деплоить при живом Runner (свой трей не поднимать)
      powershell -ExecutionPolicy Bypass -File deploy80.ps1 -SkipBackup     # выкатиться без предделойного снимка данных
      ... -PublishDir 'D:\deploy\claude' -AppUrl 'https://naychenko.me' -Port 80
#>
param(
    [switch]$SkipFrontend,
    [switch]$NoRestart,
    [switch]$SkipSandbox,      # не трогать образ песочницы claude-sandbox
    [switch]$SandboxNoCache,   # пересобрать образ песочницы начисто (свежий claude CLI из npm)
    [switch]$Console,          # запускать сервер прямым процессом (старый режим), без трея
    [switch]$NoAutostart,      # не создавать/обновлять ярлык автозапуска трея
    [switch]$IgnoreRunner,     # не прерываться из-за запущенного ClaudeCodeServerRunner
    [switch]$SkipBackup,       # выкатываться без предделойного снимка данных (осознанно)
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

# --- 0. Оркестратор ClaudeCodeServerRunner (соседняя репа) ---
# Runner — внешний трей-оркестратор стека (Proxifier -> продукт -> База знаний). Продукт он
# держит СВОИМ супервизором, с авто-рестартом. Деплой поверх живого Runner ломается дважды:
# на шаге 1 мы гасим ClaudeHomeServer.exe, Runner тут же поднимает его обратно и лочит файлы
# публикации; а если дойти до конца — в трее окажутся два супервизора на одном порту 80.
# Симптомы у обоих отвратительные, поэтому останавливаемся заранее и говорим прямо.
# Сам Runner своё обновление от нашего трея уже страхует (у него он исключён из деплоя),
# но обратной защиты не было: прямой запуск этого скрипта мимо Runner её и добавляет.
$runner = @(Get-Process ClaudeServerTray -ErrorAction SilentlyContinue)
$runnerActive = $runner.Count -gt 0
if ($runnerActive -and -not $IgnoreRunner) {
    Write-Host ''
    Write-Host 'ОСТАНОВЛЕНО: запущен ClaudeCodeServerRunner (ClaudeServerTray.exe)' -ForegroundColor Red
    Write-Host "  PID: $($runner.Id -join ', ')" -ForegroundColor DarkGray
    Write-Host '  Он супервизит продукт и поднимет сервер обратно посреди публикации.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Выйди из Runner через меню трея и повтори деплой,' -ForegroundColor Yellow
    Write-Host '  либо запусти с -IgnoreRunner: тогда свой трей-супервизор и ярлык' -ForegroundColor Yellow
    Write-Host '  автозапуска ставиться не будут, а сервер поднимет сам Runner.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}
if ($runnerActive) {
    Write-Host '[0/9] Runner запущен, но -IgnoreRunner задан: свой трей и автозапуск пропускаем' -ForegroundColor DarkYellow
}

# --- 0.5 Бэкап данных перед выкаткой ---
# Самая дешёвая страховка: снимок делается секунды и весит пару мегабайт, а откатываться
# после неудачного деплоя больше нечем. Снимаем ДО остановки сервера — при живом сервере
# это безопасно (json-сторы атомарны, SQLite снимается online-backup API).
# Провал снимка ОСТАНАВЛИВАЕТ деплой. Раньше он печатал жёлтую строчку и выкатка ехала
# дальше — то есть страховка отказывала молча, а узнать об этом можно было только в тот
# момент, когда она понадобится. 25.07.2026 так и вышло: боевой exe был старее самого
# флага --backup, принял его за аргумент веб-хоста, вместо снимка поднял сервер на две
# минуты (до Kill по таймауту) — и деплой спокойно продолжился без единого архива.
# Нужна выкатка любой ценой — это осознанный -SkipBackup, а не молчаливый обход.
$serverExe = Join-Path $PublishDir 'ClaudeHomeServer.exe'
if ($SkipBackup) {
    Write-Host '[0.5/9] Бэкап пропущен (-SkipBackup)' -ForegroundColor DarkYellow
} elseif (Test-Path $serverExe) {
    Write-Host '[0.5/9] Бэкап данных перед деплоем...' -ForegroundColor Yellow
    $backupError = $null
    try {
        # Окружение процесс унаследует от скрипта: $env:ASPNETCORE_ENVIRONMENT выставлен
        # выше (строка 45). Это важно — от него зависит, какой appsettings.{Environment}.json
        # прочитается (DataPath и пути бэкапа) и какой боевой конфиг попадёт в архив
        # секретов. Параметр -Environment у Start-Process тут неприменим: он появился
        # только в PowerShell 7, а скрипт заявлен как #Requires -Version 5.1.
        $backup = Start-Process -FilePath $serverExe -ArgumentList '--backup' `
            -WorkingDirectory $PublishDir -NoNewWindow -PassThru
        # Обращение к Handle кеширует дескриптор процесса, и без него ExitCode приходит
        # ПУСТЫМ даже после успешного завершения: PowerShell закрывает свой хэндл, а .NET
        # без него код возврата у мёртвого процесса уже не достанет. Замер на этой машине:
        # `cmd /c exit 0` через Start-Process -PassThru даёт ExitCode='' без этой строки
        # и 0 с ней. Пустота != 0, поэтому проверка ниже принимала удачный снимок за провал.
        $null = $backup.Handle
        if ($backup.WaitForExit(120000)) {
            # Код возврата тут честен: BackupCli ставит ExitCode=1, когда снимок не удался,
            # и 0 только после реально записанного архива — сверять файлы на диске не нужно.
            if ($backup.ExitCode -eq 0) { Write-Host '  готово' -ForegroundColor DarkGray }
            else { $backupError = "бэкап вернул код $($backup.ExitCode)" }
        } else {
            # Kill() без параметров: перегрузка Kill($true) — это .NET Core / PowerShell 7,
            # а скрипт живёт на Windows PowerShell 5.1 (.NET Framework), где её нет.
            # Дочерних процессов у --backup всё равно не бывает.
            try { $backup.Kill() } catch { }
            $backupError = 'бэкап не уложился в 2 минуты и был прерван ' +
                           '(похоже, опубликованная сборка ещё не знает флага --backup)'
        }
    } catch {
        $backupError = "бэкап не выполнен: $($_.Exception.Message)"
    }
    if ($backupError) {
        Write-Host ''
        Write-Host "ОСТАНОВЛЕНО: $backupError" -ForegroundColor Red
        Write-Host '  Снимка перед выкаткой нет — откатываться после неудачного деплоя будет нечем.' -ForegroundColor Yellow
        Write-Host '  Разберись с бэкапом либо запусти с -SkipBackup, если выкатка нужна прямо сейчас.' -ForegroundColor Yellow
        Write-Host ''
        exit 1
    }
} else {
    Write-Host '[0.5/9] Бэкап пропущен: первый деплой (сервер ещё не опубликован)' -ForegroundColor DarkGray
}

# --- 1. Остановка запущенных процессов (снять локи файлов ДО публикации) ---
# Трей глушим ПЕРВЫМ, чтобы его супервизор не перезапустил сервер, пока мы его убиваем.
Write-Host '[1/9] Остановка запущенных процессов...' -ForegroundColor Yellow
Get-Process ClaudeHomeServer.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Get-Process ClaudeHomeServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

# --- 2. Сборка фронта ---
if (-not $SkipFrontend) {
    Write-Host '[2/9] Сборка фронта (npm run build)...' -ForegroundColor Yellow
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
    Write-Host '[2/9] Фронт пропущен (-SkipFrontend)' -ForegroundColor DarkGray
}

# --- 3. Публикация бэка ---
Write-Host '[3/9] Публикация бэка (dotnet publish -c Release)...' -ForegroundColor Yellow
dotnet publish $csproj -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация бэка упала (exit $LASTEXITCODE)" }

# --- 3.1 Обеспечить appsettings.Production80.json (gitignored, машинно-специфичный) ---
# При Environment=Production80 ASP.NET Core грузит именно его; без него Kestrel поднимается
# на дефолтном порту 5000. Если файла в папке публикации нет — создаём из Production.json.
#
# ВАЖНО, где его править. Файл в .gitignore, но у Web SDK любой appsettings*.json из папки
# проекта — это Content, и publish кладёт его в $PublishDir, ПЕРЕЗАПИСЫВАЯ тамошний
# (исключён из publish только appsettings.Local.json, см. ClaudeHomeServer.csproj).
# Поэтому:
#   • есть копия в backend\ClaudeHomeServer\ — источник правды ОНА, правки прямо в
#     C:\deploy\claude\appsettings.Production80.json молча теряются на следующем деплое;
#   • нет копии в проекте (чистый клон) — тогда боевой файл живёт своей жизнью и
#     сохраняется, как и написано ниже про первый деплой.
# Прежний комментарий утверждал «правки сохраняются» безусловно — верно только для второго
# случая. На машине с копией в проекте это неправда, и расхождение обнаружилось так:
# боевой файл 23.07 был без секции Telemetry, а проектный — с ней.
#
# Боевые секреты и пути (ключи LLM, DataPath, UserHomeOverrides) живут в
# appsettings.Local.json — его publish не трогает (CopyToPublishDirectory=Never).
$prod80 = Join-Path $PublishDir 'appsettings.Production80.json'
if (-not (Test-Path $prod80)) {
    Copy-Item (Join-Path $repo 'backend\ClaudeHomeServer\appsettings.Production.json') $prod80
    Write-Host '  appsettings.Production80.json создан из Production.json (первый деплой)' -ForegroundColor DarkGray
}

# --- 4. Публикация ConPTY-моста терминала (рядом с сервером) ---
# ConPtyBridgeLocator ищет ConPtyBridge.exe от AppContext.BaseDirectory; без него
# веб-терминал молча деградирует в голое перенаправление (без backspace/стрелок)
Write-Host '[4/9] Публикация ConPTY-моста терминала...' -ForegroundColor Yellow
dotnet publish (Join-Path $repo 'backend\ConPtyBridge\ConPtyBridge.csproj') -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация ConPtyBridge упала (exit $LASTEXITCODE)" }

# --- 5. Публикация трей-супервизора (рядом с сервером) ---
if (-not $Console) {
    Write-Host '[5/9] Публикация трей-супервизора...' -ForegroundColor Yellow
    dotnet publish $trayproj -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "Публикация трея упала (exit $LASTEXITCODE)" }
    # Конфиг трея: окружение дочернего сервера и URL «Открыть в браузере» — из параметров деплоя.
    $trayCfg = [ordered]@{ ServerExe = 'ClaudeHomeServer.exe'; Environment = $Environment; Url = $AppUrl; Port = $Port }
    ($trayCfg | ConvertTo-Json) | Set-Content -Path (Join-Path $PublishDir 'tray.json') -Encoding UTF8
} else {
    Write-Host '[5/9] Трей пропущен (-Console)' -ForegroundColor DarkGray
}

# --- 5. Свежий фронт в wwwroot (рядом с exe) ---
Write-Host '[6/9] Копирование фронта в wwwroot...' -ForegroundColor Yellow
$wwwroot = Join-Path $PublishDir 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item "$wwwroot\*" -Recurse -Force -ErrorAction Stop }
else { New-Item -ItemType Directory -Force $wwwroot -ErrorAction Stop | Out-Null }
Copy-Item (Join-Path $frontendDir 'dist\*') $wwwroot -Recurse -Force -ErrorAction Stop

# --- 6. MCP-серверы (чистый Node, сборка не нужна) ---
Write-Host '[7/9] Копирование MCP-серверов...' -ForegroundColor Yellow
foreach ($srv in 'tasks-server', 'notes-server', 'memory-server', 'personas-server', 'workspace-server', 'notifications-server', 'widgets-server') {
    $mcpDst = Join-Path $PublishDir "mcp\$srv"
    New-Item -ItemType Directory -Force $mcpDst -ErrorAction Stop | Out-Null
    Copy-Item (Join-Path $repo "mcp\$srv\*") $mcpDst -Recurse -Force -ErrorAction Stop
}
# mcp-dify (TypeScript, dist собран заранее + node_modules): нужен рядом с сервером —
# DockerPathMapper мапит {PublishDir}/mcp-dify -> /app/mcp-dify, иначе dify в песочнице
# молча пропускается (путь из .mcp.json не переводится в контейнерный)
$difySrc = Join-Path $repo 'mcp-dify'
if (Test-Path (Join-Path $difySrc 'dist\index.js')) {
    $difyDst = Join-Path $PublishDir 'mcp-dify'
    if (Test-Path $difyDst) { Remove-Item $difyDst -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Force $difyDst -ErrorAction Stop | Out-Null
    foreach ($item in 'dist', 'node_modules', 'package.json') {
        Copy-Item (Join-Path $difySrc $item) $difyDst -Recurse -Force -ErrorAction Stop
    }
} else {
    Write-Host '  mcp-dify пропущен: нет dist/index.js (собери mcp-dify перед деплоем)' -ForegroundColor DarkYellow
}

# --- 7. Пересборка образа песочницы (синхронизация с версией хоста) ---
# Образ claude-sandbox несёт В СЕБЕ код MCP-серверов (/app/mcp), run-turn.sh, claude-defaults
# и сам claude CLI. Бэкенд на хосте свежий, но ходы container-юзеров и их MCP исполняются
# ВНУТРИ песочницы из образа — если его не пересобрать, доработки после последней сборки
# образа молча не работают в песочнице (рассинхрон). Поэтому пересборка — часть деплоя.
if ($SkipSandbox) {
    Write-Host '[8/9] Песочница пропущена (-SkipSandbox)' -ForegroundColor DarkGray
} else {
    Write-Host '[8/9] Пересборка образа песочницы claude-sandbox...' -ForegroundColor Yellow
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
# При живом Runner ярлык не трогаем: два супервизора в «Автозагрузке» подняли бы сервер дважды.
if (-not $Console -and -not $NoAutostart -and -not $runnerActive) {
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
    Write-Host '[9/9] Запуск пропущен (-NoRestart)' -ForegroundColor DarkGray
} elseif ($runnerActive) {
    # Свой трей не поднимаем: продуктом управляет Runner, он же вернёт сервер после нашего стопа
    Write-Host '[9/9] Свой трей не запускаем — продуктом управляет Runner' -ForegroundColor DarkYellow
    Write-Host '      Подними сервер из его меню, если он не сделал этого сам.' -ForegroundColor DarkGray
} elseif ($Console) {
    Write-Host '[9/9] Запуск сервера (консольный режим)...' -ForegroundColor Yellow
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.exe') -WorkingDirectory $PublishDir
} else {
    Write-Host '[9/9] Запуск трей-супервизора (он поднимет сервер)...' -ForegroundColor Yellow
    Start-Process (Join-Path $PublishDir 'ClaudeHomeServer.Tray.exe') -WorkingDirectory $PublishDir
}

Write-Host ''
if ($NoRestart -or $runnerActive) {
    Write-Host 'Готово. Сборка опубликована, запуск сервера — за тобой.' -ForegroundColor Green
} else {
    Write-Host "Готово. Сервер запущен на порту $Port." -ForegroundColor Green
}
