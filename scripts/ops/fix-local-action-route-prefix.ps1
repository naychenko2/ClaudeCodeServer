<#
.SYNOPSIS
    Разовая починка маршрутов локальных действий, сохранённых голым именем модели
    прямого адаптера (без префикса direct:).

.DESCRIPTION
    Дефект: в data/local-actions.json место применения модели записано как «auto:smart»
    вместо «direct:auto:smart». Модель та же, но без префикса значение попадает в форму
    «id модели провайдера» (ADR-009 §1, форма 8) и уезжает в claude CLI вместо прямого
    HTTP-адаптера. На проде это давало LlmTimeoutException на каждом вызове: место молча
    работало страховкой цепочки.

    Критерий «кривизны» берётся ИЗ КОНФИГА, а не хардкодом: чинится только то значение,
    чья модель объявлена источником прямого адаптера (CheapHttpSources:{key}:Models или
    OpenRouter:DirectModels) — то есть у которого есть канонический префиксный вид
    direct:{id} в каталоге /api/models. Модель и поставщик не меняются: правится ровно
    форма записи маршрута.

    НЕ ТРОГАЕТ:
      - служебные формы local / claude / default / tier:* / preset:* и уже префиксные direct:*;
      - голые id моделей CLI-провайдеров (fusion, MiniMax-M3 и пр.) — для них голое имя
        каноническое, а приписывание direct: увело бы вызов к чужому источнику.

.PARAMETER DataPath
    Каталог данных инстанса (там лежит local-actions.json). Прод: C:\ClaudeData\prod

.PARAMETER AppSettingsDir
    Каталог приложения с appsettings*.json — источник списка direct-моделей.
    Прод: C:\ClaudeServer\prod

.PARAMETER BackupRoot
    Куда класть бэкап затронутого файла. По умолчанию — <DataPath>\backups\ops.

.PARAMETER Apply
    Без него — сухой прогон (ничего не пишется). С ним — бэкап и правка файла.

.NOTES
    ВАЖНО: LocalActionOverridesStore читает файл ТОЛЬКО при старте и переписывает его
    целиком из своего снимка в памяти при любом сохранении из UI. Поэтому правка файла
    на живом сервере (а) не применится до рестарта и (б) будет затёрта первым же
    сохранением маршрута из раздела «Поставщики моделей». Применять при остановленном
    сервере, либо вносить те же значения через UI.

    Пошаговый порядок применения на проде (стоп через трей → -Apply → старт), пути
    бэкапов, откат и проверка результата — в README.md рядом со скриптом.

.EXAMPLE
    # сухой прогон на проде
    .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod

.EXAMPLE
    # применение (сервис остановлен)
    .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $DataPath,
    [Parameter(Mandatory = $true)] [string] $AppSettingsDir,
    [string] $BackupRoot,
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'

# Служебные формы маршрута (ADR-009 §1) — не имена моделей, префикс им не нужен
$ServiceLiterals = @('local', 'claude', 'default')
$ServicePrefixes = @('tier:', 'preset:', 'direct:')

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    # BOM ломает ConvertFrom-Json
    return ($raw -replace '^\uFEFF', '') | ConvertFrom-Json
}

# Список id моделей, обслуживаемых прямым HTTP-адаптером: OpenRouter:DirectModels (legacy)
# + CheapHttpSources:{key}:Models. Слои конфига применяются по порядку загрузки бэкенда.
function Get-DirectModelIds([string] $Dir) {
    $ids = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @('appsettings.json', 'appsettings.Production.json', 'appsettings.Local.json')) {
        $cfg = Read-JsonFile (Join-Path $Dir $name)
        if ($null -eq $cfg) { continue }

        foreach ($m in @($cfg.OpenRouter.DirectModels)) {
            if ($null -eq $m) { continue }
            $id = if ($m -is [string]) { $m } else { $m.Id }
            if ($id) { $ids.Add([string]$id) }
        }

        if ($cfg.CheapHttpSources) {
            foreach ($src in $cfg.CheapHttpSources.PSObject.Properties) {
                if ($src.Name.StartsWith('#')) { continue }   # комментарии секции
                foreach ($m in @($src.Value.Models)) {
                    if ($null -eq $m) { continue }
                    $id = if ($m -is [string]) { $m } else { $m.Id }
                    if ($id) { $ids.Add([string]$id) }
                }
            }
        }
    }
    return $ids | Sort-Object -Unique
}

function Test-ServiceRoute([string] $Route) {
    if ($ServiceLiterals -contains $Route) { return $true }
    foreach ($p in $ServicePrefixes) {
        if ($Route.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$storePath = Join-Path $DataPath 'local-actions.json'
if (-not (Test-Path -LiteralPath $storePath)) { throw "Не найден файл маршрутов: $storePath" }

$directIds = Get-DirectModelIds $AppSettingsDir
if ($directIds.Count -eq 0) { throw "В конфигах $AppSettingsDir не нашёл ни одной модели прямого адаптера — критерий проверки пуст, прекращаю" }

Write-Host "Модели прямого адаптера из конфига ($($directIds.Count)): $($directIds -join ', ')"

$routes = Read-JsonFile $storePath
if ($null -eq $routes) { throw "Файл маршрутов пуст или нечитаем: $storePath" }

# Порядок ключей сохраняем — файл переписывается целиком
$result = [ordered]@{}
$fixes = [System.Collections.Generic.List[object]]::new()

foreach ($p in $routes.PSObject.Properties) {
    $key = $p.Name
    $route = [string]$p.Value
    $value = $route

    if (-not (Test-ServiceRoute $route)) {
        # голое имя модели: чиним, только если эта модель обслуживается прямым адаптером
        $match = $directIds | Where-Object { $_ -ieq $route.Trim() } | Select-Object -First 1
        if ($match) {
            $value = "direct:$match"
            $fixes.Add([pscustomobject]@{ Action = $key; From = $route; To = $value })
        }
    }

    $result[$key] = $value
}

Write-Host ""
if ($fixes.Count -eq 0) {
    Write-Host "Записей с маршрутом без префикса не найдено — чинить нечего."
    return
}

Write-Host "Найдено записей к починке: $($fixes.Count)"
$fixes | Format-Table -AutoSize | Out-String | Write-Host

if (-not $Apply) {
    Write-Host "Сухой прогон: файл не изменён. Для применения повторить с -Apply (сервис должен быть остановлен)."
    return
}

# Бэкап затронутого файла до правки
if (-not $BackupRoot) { $BackupRoot = Join-Path $DataPath 'backups\ops' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $BackupRoot "local-action-route-prefix-$stamp"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
Copy-Item -LiteralPath $storePath -Destination (Join-Path $backupDir 'local-actions.json')
$fixes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupDir 'changes.json') -Encoding utf8
Write-Host "Бэкап: $backupDir"

# Формат как у сервера: компактный JSON одной строкой, UTF-8 без BOM. Пишем через временный
# файл — оборванная запись не должна оставить нечитаемый стор (Load тогда игнорирует весь файл).
$json = ($result | ConvertTo-Json -Depth 4 -Compress)
$tmp = "$storePath.tmp"
[System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -LiteralPath $tmp -Destination $storePath -Force

Write-Host "Готово: $storePath обновлён ($($fixes.Count) записей)."
Write-Host "Напоминание: значения подхватятся при старте сервиса — стор читает файл только при загрузке."
