#Requires -Version 5.1
<#
    Установка/обновление Viaduct Community (C4-редактор) для раздела «Архитектура» CCS.

    Клонирует закреплённый коммит во временную папку, собирает, пост-обрабатывает dist под
    раздачу по префиксу /modules/viaduct/ и атомарно подменяет {DataDir}\modules\viaduct\dist.
    Код Viaduct в репу CCS не попадает; сборка — воспроизводимый кеш, в бэкап не едет.
    Сервер подхватывает новую сборку без рестарта (ViaductStaticHosting).
    План встраивания — docs/research/viaduct-embed-plan.md.

    Monaco (редакторы документов, sequence, контрактов) Viaduct штатно берёт с CDN, а CSP
    раздачи пускает только 'self'. Поэтому AMD-сборка monaco-editor (min/vs, версия закреплена
    -MonacoVersion) кладётся рядом, в dist/monaco/vs, а шим поднимает её до старта бандла.

    Источник сборки (репа + коммит) выбирается так: явные -RepoUrl/-Commit → машинные
    переменные окружения VIADUCT_REPO_URL/VIADUCT_COMMIT (задаются на машине, в git не
    живут) → публичный апстрим на закреплённом коммите. Переменные задаются парой: одна без
    другой — ошибка. Доступ к закрытой репе — через Git Credential Manager машины, токены
    скрипт не принимает и никуда не пишет.

    Идемпотентен: если в целевой папке уже лежит сборка того же источника (репа + коммит, тот
    же шим и та же версия Monaco) — ничего не делает. Пересобрать принудительно — -Force.
    Обновление Viaduct — только осознанной сменой коммита (структура index.html может
    поменяться: пост-обработка тогда падает громко, а старая сборка остаётся на месте).

    Использование:
      # дев-стенд (dotnet run: data лежит рядом с exe)
      powershell -ExecutionPolicy Bypass -File scripts\build-viaduct.ps1 -DataDir backend\ClaudeHomeServer\bin\Debug\net10.0\data
      # бой
      powershell -ExecutionPolicy Bypass -File scripts\build-viaduct.ps1 -DataDir C:\deploy\claude\data
      # шим localStorage (scripts\viaduct-shim.js) инжектится по умолчанию; свой — -ShimPath,
      # сборка без шима — -NoShim

    Откат: удалить папку {DataDir}\modules\viaduct — раздел покажет «модуль не установлен»;
    либо прогнать скрипт с прежним источником (-RepoUrl/-Commit или переменными окружения).

    Требует: git, node 22 (по .nvmrc Viaduct), npm. Сеть — хостинг репы и npm registry.
    Коды возврата: 0 — установлено или уже актуально, 1 — провал (прежняя сборка не тронута).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DataDir,                                               # каталог data инстанса CCS
    [string]$Commit,                                                # коммит (полный SHA); по умолчанию см. «Источник сборки»
    [string]$RepoUrl,                                               # репа; по умолчанию см. «Источник сборки»
    [string]$MonacoVersion = '0.56.0',                              # monaco-editor из lock-файла закреплённого коммита
    [string]$ShimPath,                                              # шим localStorage (по умолчанию scripts\viaduct-shim.js)
    [switch]$NoShim,                                                # собрать без шима (отладка сборки)
    [switch]$Force,                                                 # пересобрать, даже если актуально
    [switch]$KeepWorkDir                                            # не удалять временный клон (отладка)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Префикс раздачи — должен совпадать с ViaductStaticHosting.RequestPath
$Base = '/modules/viaduct/'
$MarkerName = '.viaduct-build.json'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "ОШИБКА: $Text" -ForegroundColor Red; exit 1 }

function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$What) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$What завершился с кодом $LASTEXITCODE" }
}

function Get-FileSha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

# UTF-8 без BOM: браузер BOM переживёт, но лишние байты в начале index.html ни к чему
$Utf8 = New-Object System.Text.UTF8Encoding($false)
function Read-Text([string]$Path) { [IO.File]::ReadAllText($Path, $Utf8) }
function Write-Text([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $Utf8) }

# --- Источник сборки ---------------------------------------------------------------------
$DefaultRepoUrl = 'https://github.com/quietgridlabs/viaduct'
$DefaultCommit = '1af2d6e21e2ab66e8e62caf67063eac7fc1dfd57'     # закреплённый коммит апстрима (тег v0.1.2)
if (-not $RepoUrl -and -not $Commit) {
    $envRepo = $env:VIADUCT_REPO_URL
    $envCommit = $env:VIADUCT_COMMIT
    if ($envRepo -and $envCommit) {
        $RepoUrl = $envRepo.Trim(); $Commit = $envCommit.Trim().ToLowerInvariant()
        Write-Host 'Источник сборки — переменные окружения VIADUCT_REPO_URL/VIADUCT_COMMIT'
    } elseif ($envRepo -or $envCommit) {
        Fail 'VIADUCT_REPO_URL и VIADUCT_COMMIT задаются парой — задана только одна из переменных'
    }
}
if (-not $RepoUrl) { $RepoUrl = $DefaultRepoUrl }
if (-not $Commit) {
    # Коммит апстрима к чужой репе не подходит: без явного коммита такая сборка — ошибка
    if ($RepoUrl -ne $DefaultRepoUrl) { Fail "для репы, отличной от апстрима, нужен явный -Commit (или VIADUCT_COMMIT)" }
    $Commit = $DefaultCommit
}
# Пароль/токен в адресе не принимаем — доступ даёт Git Credential Manager. Логин без пароля
# штатен (у Azure DevOps адрес вида https://org@host/...), но на его месте мог оказаться
# токен: в лог и маркер идёт адрес без userinfo
if ($RepoUrl -match '^[a-z][a-z0-9+.-]*://[^/@]*:[^/@]*@') { Fail 'в адресе репы не должно быть логина с паролем/токеном — доступ даёт Git Credential Manager' }
$RepoUrlSafe = $RepoUrl -replace '^([a-z][a-z0-9+.-]*://)[^/@]*@', '$1'

# --- Проверки входа ----------------------------------------------------------------------
if ($Commit -notmatch '^[0-9a-f]{40}$') { Fail "-Commit должен быть полным SHA (40 hex), получено «$Commit»" }
if ($MonacoVersion -notmatch '^\d+\.\d+\.\d+$') { Fail "-MonacoVersion должен быть точной версией X.Y.Z, получено «$MonacoVersion»" }
foreach ($tool in 'git', 'node', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "не найден $tool в PATH" }
}
$nodeMajor = [int]((& node --version).TrimStart('v').Split('.')[0])
if ($nodeMajor -lt 22) { Fail "нужен Node 22+ (по .nvmrc Viaduct), найден $(& node --version)" }

$shimHash = $null
# Без шима модель не переживает перезагрузку фрейма — поэтому он включён по умолчанию
if (-not $ShimPath -and -not $NoShim) { $ShimPath = Join-Path $PSScriptRoot 'viaduct-shim.js' }
if ($NoShim) { $ShimPath = $null }
if ($ShimPath) {
    if (-not (Test-Path -LiteralPath $ShimPath -PathType Leaf)) { Fail "шим не найден: $ShimPath" }
    $ShimPath = (Resolve-Path -LiteralPath $ShimPath).Path
    $shimHash = Get-FileSha256 $ShimPath
}

$DataDir = [IO.Path]::GetFullPath($DataDir)
$TargetRoot = Join-Path $DataDir 'modules\viaduct'
$DistDir = Join-Path $TargetRoot 'dist'
$MarkerPath = Join-Path $DistDir $MarkerName

# --- Идемпотентность ---------------------------------------------------------------------
if (-not $Force -and (Test-Path -LiteralPath $MarkerPath) -and (Test-Path -LiteralPath (Join-Path $DistDir 'index.html'))) {
    try { $marker = Read-Text $MarkerPath | ConvertFrom-Json } catch { $marker = $null }
    # StrictMode бросает на отсутствующем свойстве: маркер старой сборки без поля monaco — не актуален
    $markerMonaco = if ($marker -and $marker.PSObject.Properties['monaco']) { $marker.monaco } else { $null }
    $markerRepo = if ($marker -and $marker.PSObject.Properties['repoUrl']) { $marker.repoUrl } else { $null }
    if ($marker -and $marker.commit -eq $Commit -and $markerRepo -eq $RepoUrlSafe -and $marker.base -eq $Base -and $marker.shimSha256 -eq $shimHash -and $markerMonaco -eq $MonacoVersion) {
        Write-Host "Viaduct $($Commit.Substring(0, 8)) уже установлен в $DistDir — ничего не делаю (-Force для пересборки)" -ForegroundColor Green
        exit 0
    }
}

# --- Клон на закреплённом коммите ----------------------------------------------------------
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("ccs-viaduct-build-" + $Commit.Substring(0, 12))
if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir | Out-Null

Push-Location $WorkDir
try {
    Write-Step "Клон $RepoUrlSafe @ $Commit"
    Invoke-Native git @('init', '-q') 'git init'
    Invoke-Native git @('remote', 'add', 'origin', $RepoUrl) 'git remote add'
    Invoke-Native git @('fetch', '-q', '--depth', '1', 'origin', $Commit) 'git fetch'
    Invoke-Native git @('checkout', '-q', 'FETCH_HEAD') 'git checkout'
    $head = (& git rev-parse HEAD).Trim()
    if ($head -ne $Commit) { Fail "после checkout HEAD=$head, ожидался $Commit" }

    # --- Сборка ------------------------------------------------------------------------------
    # npm run build не годится как есть: --base не прокинуть (хвост аргументов уедет в
    # prerender), а prerender (снимки лендинга для SEO) встраиванию не нужен. Зовём шаги
    # build напрямую. --ignore-scripts: prepare=husky хочет git-хуки, установке не нужен.
    $env:VITE_METRICS_DISABLED = '1'        # продуктовые метрики Viaduct — выключены
    $env:VITE_UMAMI_WEBSITE_ID = ''         # Umami — выключен
    $env:VITE_EDITION = 'community'
    Write-Step 'npm ci'
    Invoke-Native npm @('ci', '--no-audit', '--no-fund', '--ignore-scripts') 'npm ci'
    Write-Step 'vendorRedoc + tsc -b + vite build'
    Invoke-Native node @('scripts/vendorRedoc.mjs') 'vendorRedoc'
    Invoke-Native npx @('--no-install', 'tsc', '-b') 'tsc -b'
    Invoke-Native npx @('--no-install', 'vite', 'build', "--base=$Base") 'vite build'

    $BuildDist = Join-Path $WorkDir 'dist'
    $indexPath = Join-Path $BuildDist 'index.html'
    if (-not (Test-Path -LiteralPath $indexPath)) { Fail "vite build не создал $indexPath" }

    # --- Пост-обработка под префикс ----------------------------------------------------------
    # Vite переписывает под --base только то, что проходит через его конвейер; файлы из
    # public/ копируются как есть, и корневые /fonts/, /vendor/, иконки в них смотрят мимо
    # префикса. Правим их здесь, код Viaduct не трогаем.
    Write-Step 'Пост-обработка dist'
    $escBase = [regex]::Escape($Base.TrimStart('/'))
    foreach ($css in Get-ChildItem -LiteralPath $BuildDist -Filter *.css -File) {
        $text = Read-Text $css.FullName
        $text = [regex]::Replace($text, "url\(\s*(['""]?)/(?!/|$escBase)", "url(`$1$Base")
        Write-Text $css.FullName $text
    }
    foreach ($html in Get-ChildItem -LiteralPath $BuildDist -Filter *.html -File) {
        $text = Read-Text $html.FullName
        $text = [regex]::Replace($text, "(<(?:script|link|img)\b[^>]*?\s(?:src|href)=)""/(?!/|$escBase)", "`$1""$Base")
        Write-Text $html.FullName $text
    }
    $manifest = Join-Path $BuildDist 'manifest.webmanifest'
    if (Test-Path -LiteralPath $manifest) {
        $text = Read-Text $manifest
        $text = [regex]::Replace($text, """(src|start_url|scope)""(\s*):(\s*)""/(?!/|$escBase)", """`$1""`$2:`$3""$Base")
        Write-Text $manifest $text
    }

    # --- Локальный Monaco -------------------------------------------------------------------
    # Берём из node_modules Viaduct (тот же пакет, против которого собран бандл); если lock-файл
    # коммита держит другую версию — ставим закреплённую отдельно, мимо дерева Viaduct.
    Write-Step "Monaco $MonacoVersion"
    $monacoPkg = Join-Path $WorkDir 'node_modules\monaco-editor'
    $monacoFound = if (Test-Path -LiteralPath (Join-Path $monacoPkg 'package.json')) { (Read-Text (Join-Path $monacoPkg 'package.json') | ConvertFrom-Json).version } else { $null }
    if ($monacoFound -ne $MonacoVersion) {
        Write-Host "в lock-файле Viaduct monaco-editor $monacoFound, ставлю закреплённый $MonacoVersion отдельно"
        $monacoPrefix = Join-Path $WorkDir '.ccs-monaco'
        New-Item -ItemType Directory -Path $monacoPrefix -Force | Out-Null
        Invoke-Native npm @('install', '--prefix', $monacoPrefix, '--no-save', '--no-audit', '--no-fund', '--ignore-scripts', "monaco-editor@$MonacoVersion") 'npm install monaco-editor'
        $monacoPkg = Join-Path $monacoPrefix 'node_modules\monaco-editor'
        $monacoFound = (Read-Text (Join-Path $monacoPkg 'package.json') | ConvertFrom-Json).version
        if ($monacoFound -ne $MonacoVersion) { Fail "установлен monaco-editor $monacoFound, ожидался $MonacoVersion" }
    }
    $monacoSrc = Join-Path $monacoPkg 'min\vs'
    if (-not (Test-Path -LiteralPath (Join-Path $monacoSrc 'loader.js'))) { Fail "в $monacoSrc нет loader.js — раскладка monaco-editor поменялась" }
    $monacoDst = Join-Path $BuildDist 'monaco\vs'
    New-Item -ItemType Directory -Path (Split-Path $monacoDst) -Force | Out-Null
    Copy-Item -LiteralPath $monacoSrc -Destination $monacoDst -Recurse
    # Самодостаточные воркеры (без importScripts): только такие живут, запущенные из blob
    $workers = [ordered]@{}
    foreach ($label in 'editor', 'json') {
        $found = @(Get-ChildItem -LiteralPath (Join-Path $monacoDst 'assets') -Filter "$label.worker-*.js" -File)
        if ($found.Count -ne 1) { Fail "в monaco/vs/assets ожидался ровно один $label.worker-*.js, найдено $($found.Count)" }
        $workers[$label] = "${Base}monaco/vs/assets/$($found[0].Name)"
    }
    $monacoConfig = [ordered]@{ vs = "${Base}monaco/vs"; workers = $workers } | ConvertTo-Json -Compress

    # Шим localStorage (опционально): инжект первым в <head> + дезактивация module-скрипта
    # бандла — его запустит шим, когда модель приедет из хоста (протокол ready/init) и
    # поднимется локальный Monaco. Без шима Monaco лежит в сборке, но не подключён.
    if ($ShimPath) {
        Copy-Item -LiteralPath $ShimPath -Destination (Join-Path $BuildDist 'viaduct-shim.js') -Force
        $text = Read-Text $indexPath
        $moduleRx = '<script type="module" crossorigin src="([^"]+)"></script>'
        $found = [regex]::Matches($text, $moduleRx).Count
        if ($found -ne 1) { Fail "в index.html ожидался ровно один module-скрипт бандла, найдено $found — структура сборки Viaduct поменялась" }
        $text = [regex]::Replace($text, $moduleRx, '<script type="text/x-viaduct-app" data-src="$1"></script>')
        $headRx = '<head>'
        if (-not $text.Contains($headRx)) { Fail 'в index.html нет <head>' }
        # Порядок важен: шим → описание Monaco → AMD-загрузчик (к DOMContentLoaded он уже
        # выполнен, и шим зовёт его require). В JSON-блоке '<' не бывает: пути — наши
        $inject = "<head>`n    <script src=""${Base}viaduct-shim.js""></script>" +
            "`n    <script type=""application/json"" id=""viaduct-monaco"">$monacoConfig</script>" +
            "`n    <script src=""${Base}monaco/vs/loader.js""></script>"
        $text = ([regex]$headRx).Replace($text, $inject, 1)
        Write-Text $indexPath $text
    }

    # Контроль: ни одной корневой ссылки на ресурс мимо префикса (падаем громко — лучше
    # старая сборка, чем редактор без шрифтов и иконок в iframe)
    $leaks = @()
    foreach ($f in Get-ChildItem -LiteralPath $BuildDist -File | Where-Object { $_.Extension -in '.html', '.css', '.webmanifest' }) {
        $text = Read-Text $f.FullName
        $rx = switch ($f.Extension) {
            '.css' { "url\(\s*['""]?/(?!/|$escBase)" }
            '.html' { "<(?:script|link|img)\b[^>]*?\s(?:src|href)=""/(?!/|$escBase)" }
            default { """(?:src|start_url|scope)""\s*:\s*""/(?!/|$escBase)" }
        }
        foreach ($m in [regex]::Matches($text, $rx)) { $leaks += "$($f.Name): $($m.Value)" }
    }
    if ($leaks.Count -gt 0) { Fail ("остались ссылки мимо префикса ${Base}:`n  " + ($leaks -join "`n  ")) }

    $markerObj = [ordered]@{
        commit     = $Commit
        repoUrl    = $RepoUrlSafe
        base       = $Base
        shimSha256 = $shimHash
        monaco     = $MonacoVersion
        builtAt   = (Get-Date).ToUniversalTime().ToString('o')
        node       = (& node --version).Trim()
    }
    Write-Text (Join-Path $BuildDist $MarkerName) ($markerObj | ConvertTo-Json)

    # --- Атомарная подмена -------------------------------------------------------------------
    # Копия рядом с целью (тот же том) → rename. Сервер резолвит файлы по пути на каждый
    # запрос, окно без сборки — между двумя rename. Остатки прошлых прерванных прогонов чистим.
    Write-Step "Установка в $DistDir"
    New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    $newDir = Join-Path $TargetRoot 'dist.new'
    $oldDir = Join-Path $TargetRoot 'dist.old'
    foreach ($d in $newDir, $oldDir) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    Copy-Item -LiteralPath $BuildDist -Destination $newDir -Recurse
    if (Test-Path -LiteralPath $DistDir) { Rename-Item -LiteralPath $DistDir -NewName 'dist.old' }
    Rename-Item -LiteralPath $newDir -NewName 'dist'
    if (Test-Path -LiteralPath $oldDir) {
        # Файл старой сборки может держать открытым идущий запрос — не повод падать
        try { Remove-Item -LiteralPath $oldDir -Recurse -Force } catch { Write-Warning "не удалось удалить $oldDir — удалится следующим прогоном" }
    }

    Write-Host "Viaduct $($Commit.Substring(0, 8)) установлен: $DistDir (раздаётся по $Base)" -ForegroundColor Green
}
finally {
    Pop-Location
    if (-not $KeepWorkDir -and (Test-Path -LiteralPath $WorkDir)) {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
