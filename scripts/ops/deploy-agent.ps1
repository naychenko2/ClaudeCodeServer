#Requires -Version 5.1
<#
    Агент выкатки прода (ADR-010). Делает то же, что deploy80.ps1, но в другом порядке:
    тяжёлая сборка идёт при ЖИВОМ сервере, переключение — короткое окно, здоровье
    проверяется автоматом, при провале агент сам возвращает прошлый релиз.

    Агента запускает задача планировщика CCS-Deploy (см. scripts/ops/README.md) — так он
    не состоит в родстве с сервером и переживает его смерть. Ручной запуск тоже рабочий.

    Фазы (они же значения phase в журнале):
      queued     заявка принята, guard'ы
      building   npm build + dotnet publish в staging + docker build (сервер жив)
      switching  --backup, стоп трея и сервера, снимок релиза, staging -> PublishDir, старт
      verifying  health-гейт; не сошёлся -> автооткат
      succeeded | rolled_back | failed

    Использование:
      powershell -ExecutionPolicy Bypass -File deploy-agent.ps1                   # выкатка
      powershell -ExecutionPolicy Bypass -File deploy-agent.ps1 -DryRun           # guard'ы + план, без изменений
      powershell -ExecutionPolicy Bypass -File deploy-agent.ps1 -AllowDirty       # ехать при грязном дереве
      powershell -ExecutionPolicy Bypass -File deploy-agent.ps1 -Rollback         # откат на предыдущий релиз
      powershell -ExecutionPolicy Bypass -File deploy-agent.ps1 -Rollback -ReleaseId 20260818-135500
      ... -PublishDir 'C:\deploy\claude-test' -Environment Test80 -Port 8080      # полигон

    Коды возврата: 0 — успех, 1 — провал, 2 — отказ guard'а (ничего не трогали),
                   3 — выкатка откачена (прод жив на прошлом релизе).
#>
param(
    [switch]$Rollback,          # режим отката вместо выкатки
    [string]$ReleaseId,         # какой релиз возвращать (по умолчанию — последний снимок)
    [switch]$DryRun,            # guard'ы + план шагов, без единого изменения
    [switch]$SkipFrontend,
    [switch]$SkipSandbox,
    [switch]$AllowDirty,        # ехать при незакоммиченных правках
    [switch]$IgnoreRunner,      # ручной обход guard'а «живой Runner» (из чата не задаётся)
    [switch]$RequireBuildHeader,# требовать X-Build и при откате (в выкатке он требуется всегда)
    [switch]$NoSelfCopy,        # служебный: агент уже работает копией, не копировать себя снова
    [switch]$DirectServer,      # запускать сервер напрямую без трея (для полигона, чтобы избежать мьютекса)
    [string]$Ref,               # ожидаемая ветка; расхождение = отказ (волна 1 не переключает ref)
    [string]$RepoDir,           # корень репы (по умолчанию — от места скрипта)
    [string]$PublishDir   = 'C:\deploy\claude',
    [string]$StagingDir   = 'C:\deploy\claude.staging',
    [string]$ReleasesDir  = 'C:\deploy\claude.releases',
    [string]$AgentDir     = 'C:\deploy\ccs-deploy',
    [string]$Environment  = 'Production80',
    [string]$AppUrl       = 'https://naychenko.me',
    [int]$Port            = 80,
    [string]$HealthUrl,         # по умолчанию http://localhost:<Port>/api/health
    [int]$KeepReleases    = 3,
    [int]$HealthTimeoutSec = 90,
    [int]$HealthSuccesses  = 3,
    [int]$BackupTimeoutSec = 900   # бэкап данных полигона — секунды, прода (~19 ГБ) — 4+ минуты;
                                   # жёсткие 2 минуты из deploy80 роняли первую же боевую выкатку
)
# НЕ 'Stop' глобально: npm/dotnet пишут предупреждения в stderr, а на Windows PowerShell это
# со 'Stop' ложно роняет скрипт. Нативные проверяем по $LASTEXITCODE, критичным командлетам
# даём -ErrorAction Stop.
$ErrorActionPreference = 'Continue'

# Каталоги, которые НИКОГДА не участвуют в копировании бинарников: данные, логи, архивы
# и сертификаты живут рядом с exe и переживают любую выкатку и любой откат.
$script:DataDirs = @('data', 'logs', 'backups', 'certs')
$script:McpServers = @('tasks-server', 'notes-server', 'memory-server', 'personas-server',
                       'workspace-server', 'notifications-server', 'widgets-server')

# @($null) в PowerShell — это массив ИЗ одного $null, а не пустой: без этой обёртки
# отсутствующие в журнале history/releases превращаются в список с null-элементом.
# Запятая перед @(...) обязательна: функция, вернувшая массив из ОДНОГО элемента, отдаёт
# наружу сам элемент (конвейер разворачивает), и следующее «список + элемент» падает с
# op_Addition. Ловится только на втором шаге выкатки — то есть уже на проде.
function ConvertTo-List($value) {
    if ($null -eq $value) { return ,@() }
    return ,@($value | Where-Object { $null -ne $_ })
}

function Get-NormalizedPath([string]$p) {
    if (-not $p) { return '' }
    try { return ([IO.Path]::GetFullPath($p)).TrimEnd('\').ToLowerInvariant() } catch { return $p.ToLowerInvariant() }
}

# --- ФАЗА 0: агент работает копией себя ---------------------------------------------------
# Скрипт лежит в репе, а выкатка репу трогает (сборка, в будущем — checkout ref). Скрипт,
# переписанный посреди собственного исполнения, — классический способ получить наполовину
# выполненный пайплайн: PowerShell дочитывает файл с диска по мере исполнения. Поэтому шаг 0 копирует
# агента в свой каталог ВНЕ PublishDir и передаёт работу копии.
$selfPath = $PSCommandPath
$selfDir  = Split-Path -Parent $selfPath
$script:NeedSelfCopy = ((Get-NormalizedPath $selfDir) -ne (Get-NormalizedPath $AgentDir)) -and -not $NoSelfCopy
# Сухой прогон себя не копирует: он не меняет на диске ничего, включая каталог агента.
if ($script:NeedSelfCopy -and -not $DryRun) {
    if (-not $RepoDir) { $RepoDir = (Resolve-Path (Join-Path $selfDir '..\..')).Path }
    New-Item -ItemType Directory -Force $AgentDir -ErrorAction Stop | Out-Null
    $agentCopy = Join-Path $AgentDir 'deploy-agent.ps1'
    Copy-Item $selfPath $agentCopy -Force -ErrorAction Stop
    # Откуда копия приехала — чтобы ручной запуск уже из AgentDir знал корень репы
    Set-Content -Path (Join-Path $AgentDir 'agent-source.txt') -Value $RepoDir -Encoding UTF8
    $fwd = @()
    foreach ($k in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) {
            if ($v.IsPresent) { $fwd += "-$k" }
        } else {
            $fwd += "-$k"
            $fwd += "$v"
        }
    }
    if ($PSBoundParameters.Keys -notcontains 'RepoDir') { $fwd += '-RepoDir'; $fwd += $RepoDir }
    $fwd += '-NoSelfCopy'
    Write-Host "[0] Агент скопирован в $agentCopy, передаю работу копии" -ForegroundColor DarkGray
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $agentCopy @fwd
    exit $LASTEXITCODE
}

if (-not $RepoDir) {
    $srcMarker = Join-Path $AgentDir 'agent-source.txt'
    if (Test-Path $srcMarker) { $RepoDir = (Get-Content $srcMarker -Raw).Trim() }
}
if (-not $RepoDir) { $RepoDir = (Resolve-Path (Join-Path $selfDir '..\..')).Path }
# localhost, а не 127.0.0.1: host-фильтр прода (AllowedHosts в appsettings.Production.json)
# пускает localhost, но режет 127.0.0.1 как чужой Host — первая боевая выкатка ушла
# в автооткат и «не подняла» прод именно из-за 400 на каждый health-запрос
if (-not $HealthUrl) { $HealthUrl = "http://localhost:$Port/api/health" }

$env:ASPNETCORE_ENVIRONMENT = $Environment
$script:JournalPath = Join-Path $ReleasesDir 'deploy-state.json'
$script:Journal = $null
$script:Current = $null
$script:Mutex = $null
$script:TranscriptOn = $false

# --- Вывод --------------------------------------------------------------------------------
function Write-Info([string]$msg, [string]$color = 'Gray') {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor $color
}
function Write-Head([string]$msg) { Write-Info $msg 'Cyan' }
function Write-Warn([string]$msg) { Write-Info $msg 'DarkYellow' }
function Write-Bad([string]$msg)  { Write-Info $msg 'Red' }
function Write-Good([string]$msg) { Write-Info $msg 'Green' }

# --- Журнал -------------------------------------------------------------------------------
# Единственная точка стыковки с бэкендом: он пишет заявку (current с phase=queued) и читает
# результат. Формат — ADR-010, раздел «Журнал deploy-state.json».
function Get-Journal {
    if (Test-Path $script:JournalPath) {
        try {
            $raw = [IO.File]::ReadAllText($script:JournalPath)
            if ($raw.Trim()) { return ($raw | ConvertFrom-Json) }
        } catch {
            Write-Warn "журнал нечитаем ($($_.Exception.Message)) — начинаю новый"
        }
    }
    return [pscustomobject]@{ current = $null; history = @(); releases = @() }
}

function Write-Journal {
    if ($DryRun) { return }
    if (-not $script:Journal) { return }
    New-Item -ItemType Directory -Force $ReleasesDir -ErrorAction SilentlyContinue | Out-Null
    $json = $script:Journal | ConvertTo-Json -Depth 12
    $tmp = "$($script:JournalPath).tmp"
    # Пишем без BOM и через временный файл: журнал читает сервер на старте, недописанный
    # или BOM-нутый файл ему показывать нельзя (System.Text.Json на BOM в байтах спотыкается).
    [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $script:JournalPath -Force
}

function Set-DeployPhase([string]$phase) {
    if (-not $script:Current) { return }
    $script:Current.phase = $phase
    Write-Journal
}

function Add-DeployStep([string]$name) {
    Write-Info "-> $name" 'Yellow'
    $entry = [pscustomobject]@{ name = $name; status = 'running'; ms = 0 }
    if ($script:Current) {
        $script:Current.steps = @((ConvertTo-List $script:Current.steps) + $entry)
        Write-Journal
    }
    return [pscustomobject]@{ Entry = $entry; Watch = [Diagnostics.Stopwatch]::StartNew() }
}

function Complete-DeployStep($handle, [string]$status, [string]$message) {
    if (-not $handle) { return }
    $handle.Entry.status = $status
    $handle.Entry.ms = [int]$handle.Watch.ElapsedMilliseconds
    if ($message) {
        Add-Member -InputObject $handle.Entry -NotePropertyName 'message' -NotePropertyValue $message -Force
    }
    $tail = ''
    if ($message) { $tail = " — $message" }
    $color = 'DarkGray'
    if ($status -eq 'failed') { $color = 'Red' }
    if ($status -eq 'skipped') { $color = 'DarkYellow' }
    Write-Info ("   {0} ({1} мс){2}" -f $status, $handle.Entry.ms, $tail) $color
    Write-Journal
}

function Complete-Deploy([string]$status, [string]$message, [string]$releaseId) {
    if ($script:Current) {
        # Шаг, на котором нас выбросило исключением, остаётся в состоянии running — закрываем
        # его здесь: читающий журнал должен видеть, ГДЕ именно выкатка встала.
        foreach ($st in (ConvertTo-List $script:Current.steps)) {
            if ("$($st.status)" -eq 'running') {
                $st.status = 'failed'
                if ($message) { Add-Member -InputObject $st -NotePropertyName 'message' -NotePropertyValue $message -Force }
            }
        }
        # Через Add-Member -Force, а не присваиванием: закрывать приходится и подхваченную
        # заявку сервера, а у объекта из ConvertFrom-Json присваивание отсутствующего
        # свойства падает — тогда заявка так и осталась бы незакрытой.
        Add-Member -InputObject $script:Current -NotePropertyName 'phase' -NotePropertyValue $status -Force
        # Форма итога — ровно DeployResult бэкенда: ok/status/message/releaseId/finishedAt.
        Add-Member -InputObject $script:Current -NotePropertyName 'result' -Force -NotePropertyValue ([pscustomobject]@{
            ok         = ($status -eq 'succeeded')
            status     = $status
            message    = $message
            releaseId  = $releaseId
            finishedAt = (Get-Date).ToUniversalTime().ToString('o')
        })
        # reported НЕ трогаем: его ставит сервер, когда доложит инициатору (ADR-010).
        Write-Journal
    }
    if ($status -eq 'succeeded') { Write-Good "ИТОГ: $status — $message" }
    elseif ($status -eq 'rolled_back') { Write-Warn "ИТОГ: $status — $message" }
    else { Write-Bad "ИТОГ: $status — $message" }
}

# --- Guard'ы ------------------------------------------------------------------------------
function Enter-DeployMutex {
    # Один деплой за раз. Global\ — чтобы совпасть с бэкендом (он проверяет тот же мьютекс
    # перед приёмом заявки) и увидеть агента из любой сессии, включая сеанс планировщика.
    try {
        $script:Mutex = New-Object System.Threading.Mutex($false, 'Global\ccs-deploy')
    } catch {
        Write-Warn "мьютекс Global\ccs-deploy недоступен ($($_.Exception.Message))"
        return $false
    }
    $got = $false
    try {
        $got = $script:Mutex.WaitOne(0)
    } catch [System.Threading.AbandonedMutexException] {
        # Прошлый агент умер, не отпустив мьютекс (его же и убивают вместе с сервером в фазе 2,
        # если что-то пошло совсем не так). Брошенный мьютекс = он наш.
        $got = $true
    }
    return $got
}

function Exit-DeployMutex {
    if ($script:Mutex) {
        try { $script:Mutex.ReleaseMutex() } catch { }
        try { $script:Mutex.Dispose() } catch { }
        $script:Mutex = $null
    }
}

# Единственная дверь наружу: транскрипт и мьютекс отпускаются здесь, а не в каждой ветке.
# Windows освободил бы их и сам при выходе процесса, но «само рассосётся» не переживает
# следующую правку — а забытый Stop-Transcript ещё и оставляет обрезанный лог выкатки.
function Exit-Agent([int]$code) {
    if ($script:TranscriptOn) {
        try { Stop-Transcript | Out-Null } catch { }
        $script:TranscriptOn = $false
    }
    Exit-DeployMutex
    exit $code
}

# Отказ guard'а ПОСЛЕ подхвата заявки обязан её закрыть. Иначе в журнале навсегда остаётся
# current с phase=queued и result=null: сервер считает выкатку идущей, отвечает 409 на все
# следующие заявки и молчит инициатору — до ручной правки файла на боевой машине.
# Заявки не было (ручной запуск) — Complete-Deploy тихо ничего не пишет.
function Exit-Guard([string]$message) {
    Complete-Deploy 'failed' $message ''
    Exit-Agent 2
}

function Get-GitState {
    $state = [pscustomobject]@{ ok = $false; sha = ''; branch = ''; dirty = $false; files = @(); names = @(); error = '' }
    Push-Location $RepoDir
    try {
        $sha = (git rev-parse --short HEAD 2>&1)
        if ($LASTEXITCODE -ne 0) { $state.error = "git rev-parse: $sha"; return $state }
        $branch = (git rev-parse --abbrev-ref HEAD 2>&1)
        if ($LASTEXITCODE -ne 0) { $state.error = "git rev-parse --abbrev-ref: $branch"; return $state }
        $porcelain = @(git status --porcelain 2>&1)
        if ($LASTEXITCODE -ne 0) { $state.error = "git status: $porcelain"; return $state }
        $state.sha = "$sha".Trim()
        $state.branch = "$branch".Trim()
        $state.files = @($porcelain | Where-Object { "$_".Trim() })
        # В журнал кладём голые пути без двухбуквенного статуса — ровно так их пишет бэкенд
        $state.names = @($state.files | ForEach-Object { "$_".Substring(3).Trim() } | Where-Object { $_ })
        $state.dirty = $state.files.Count -gt 0
        $state.ok = $true
        return $state
    } finally {
        Pop-Location
    }
}

function Get-DirSizeBytes([string]$path, [string[]]$excludeTop) {
    if (-not (Test-Path $path)) { return [int64]0 }
    [int64]$total = 0
    foreach ($item in Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue) {
        if ($item.PSIsContainer) {
            if ($excludeTop -contains $item.Name) { continue }
            $m = Get-ChildItem -LiteralPath $item.FullName -Recurse -Force -File -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum
            if ($m.Sum) { $total += [int64]$m.Sum }
        } else {
            $total += [int64]$item.Length
        }
    }
    return $total
}

function Get-FreeSpaceBytes([string]$path) {
    try {
        $qualifier = Split-Path -Qualifier ([IO.Path]::GetFullPath($path))
        $disk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$qualifier'" -ErrorAction Stop
        return [int64]$disk.FreeSpace
    } catch {
        return [int64]-1
    }
}

function Test-Tool([string]$exe) {
    $cmd = Get-Command $exe -ErrorAction SilentlyContinue
    return ($null -ne $cmd)
}

# --- Копирование бинарников ---------------------------------------------------------------
# robocopy, а не Copy-Item: он умеет исключать каталоги данных одним ключом, не спотыкается
# о длинные пути и внятно отчитывается кодом возврата (<8 — успех, включая «нечего копировать»).
function Invoke-Robocopy([string]$src, [string]$dst, [switch]$Mirror, [string[]]$excludeDirs) {
    $rcArgs = @($src, $dst)
    if ($Mirror) { $rcArgs += '/MIR' } else { $rcArgs += '/E' }
    if ($excludeDirs -and $excludeDirs.Count -gt 0) {
        # Исключения даём ПОЛНЫМИ путями: голое имя robocopy трактует как маску и вырежет
        # одноимённый каталог на любом уровне вложенности (например, чей-нибудь wwwroot/data).
        $rcArgs += '/XD'
        foreach ($d in $excludeDirs) { $rcArgs += (Join-Path $src $d) }
    }
    $rcArgs += @('/R:2', '/W:1', '/NFL', '/NDL', '/NP', '/NJH', '/NJS')
    & robocopy.exe @rcArgs | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) { throw "robocopy '$src' -> '$dst' вернул $code" }
    return $code
}

function Copy-BuildTree([string]$src, [string]$dst) {
    # wwwroot / mcp / mcp-dify зеркалим: файлы, исчезнувшие из новой сборки (старые чанки
    # фронта, выпиленный MCP-сервер), обязаны исчезнуть и в публикации. Остальное — поверх.
    foreach ($mirrored in @('wwwroot', 'mcp', 'mcp-dify')) {
        $s = Join-Path $src $mirrored
        if (Test-Path $s) { Invoke-Robocopy $s (Join-Path $dst $mirrored) -Mirror | Out-Null }
    }
    Invoke-Robocopy $src $dst -excludeDirs (@('wwwroot', 'mcp', 'mcp-dify') + $script:DataDirs) | Out-Null
}

# --- Процессы -----------------------------------------------------------------------------
# Только НАШ стек: процессы, чей exe лежит в PublishDir. По одному имени Get-Process ловит
# любой одноимённый процесс на машине, а на этой же машине штатно живут хостовой дев-стенд
# (dotnet run), инспекционные копии бэкапа (--inspect) и тестовый инстанс полигона на :8080 —
# выкатка убивала бы их заодно, а потом ещё и падала на «процессы не умерли за 20 с».
# Путь недоступен (процесс чужой учётки) — значит и не наш: такие не трогаем.
function Get-StackProcesses {
    $root = Get-NormalizedPath $PublishDir
    $procs = @(Get-Process -Name 'ClaudeHomeServer', 'ClaudeHomeServer.Tray', 'ConPtyBridge' -ErrorAction SilentlyContinue)
    return @($procs | Where-Object {
        $exePath = ''
        try { $exePath = $_.Path } catch { $exePath = '' }
        if (-not $exePath) { return $false }
        return ((Get-NormalizedPath (Split-Path -Parent $exePath)) -eq $root)
    })
}

function Stop-ServerStack {
    # Трей глушим ПЕРВЫМ, иначе его супервизор поднимет сервер обратно посреди подмены файлов.
    # ConPtyBridge живёт в PublishDir и переживает смерть сервера-родителя — его exe залочит
    # копирование, поэтому он в списке наравне с сервером.
    @(Get-StackProcesses | Where-Object { $_.ProcessName -eq 'ClaudeHomeServer.Tray' }) |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    @(Get-StackProcesses | Where-Object { $_.ProcessName -ne 'ClaudeHomeServer.Tray' }) |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $alive = @(Get-StackProcesses)
        if ($alive.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $alive = @(Get-StackProcesses)
    if ($alive.Count -gt 0) { throw "процессы не умерли за 20 с: $($alive.ProcessName -join ', ')" }
    # Файловые локи снимаются не мгновенно после Exit процесса — даём Windows дописать.
    Start-Sleep -Milliseconds 800
}

function Start-ServerStack {
    if ($DirectServer) {
        $serverExe = Join-Path $PublishDir 'ClaudeHomeServer.exe'
        if (-not (Test-Path $serverExe)) { throw "не найден сервер: $serverExe" }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $serverExe
        $psi.WorkingDirectory = $PublishDir
        $psi.UseShellExecute = $false
        $psi.Environment["ASPNETCORE_ENVIRONMENT"] = $Environment
        [System.Diagnostics.Process]::Start($psi) | Out-Null
    } else {
        $trayExe = Join-Path $PublishDir 'ClaudeHomeServer.Tray.exe'
        if (-not (Test-Path $trayExe)) { throw "не найден трей-супервизор: $trayExe" }
        Start-Process -FilePath $trayExe -WorkingDirectory $PublishDir | Out-Null
    }
}

function Invoke-DataBackup {
    # Самая дешёвая страховка перед подменой бинарников: на полигоне — секунды, на проде
    # (19 ГБ данных) — 4+ минуты, потолок задаёт -BackupTimeoutSec. Делаем ДО остановки —
    # при живом сервере это безопасно (json-сторы атомарны, SQLite снимается online-backup
    # API). Провал снимка останавливает выкатку: молча отказавшая страховка хуже отсутствующей.
    $serverExe = Join-Path $PublishDir 'ClaudeHomeServer.exe'
    if (-not (Test-Path $serverExe)) { return 'первый деплой: сервер ещё не опубликован' }
    $proc = Start-Process -FilePath $serverExe -ArgumentList '--backup' -WorkingDirectory $PublishDir -NoNewWindow -PassThru
    # Обращение к Handle кеширует дескриптор: без него ExitCode у завершившегося процесса
    # приходит ПУСТЫМ, и удачный снимок читается как провал.
    $null = $proc.Handle
    if (-not $proc.WaitForExit($BackupTimeoutSec * 1000)) {
        try { $proc.Kill() } catch { }
        throw "бэкап не уложился в $BackupTimeoutSec с и был прерван (замер: 19 ГБ прода — около 250 с; зависание дольше — повод смотреть, а не ждать)"
    }
    if ($proc.ExitCode -ne 0) { throw "бэкап вернул код $($proc.ExitCode)" }
    return ''
}

# --- Health -------------------------------------------------------------------------------
function Test-HealthOnce([string]$url) {
    # HttpWebRequest, а не Invoke-WebRequest: нужен явный Proxy = $null (на машине прописан
    # egress-прокси, и запрос к 127.0.0.1 через него уходит в никуда) и чтение заголовка при
    # ответе 204, у которого тела нет.
    $res = [pscustomobject]@{ ok = $false; code = 0; build = ''; error = '' }
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method = 'GET'
        $req.Timeout = 5000
        $req.ReadWriteTimeout = 5000
        $req.Proxy = $null
        $resp = $req.GetResponse()
        $res.code = [int]$resp.StatusCode
        $res.build = "$($resp.Headers['X-Build'])"
        $resp.Close()
        $res.ok = ($res.code -ge 200 -and $res.code -lt 300)
    } catch [System.Net.WebException] {
        $res.error = $_.Exception.Message
        if ($_.Exception.Response) {
            try { $res.code = [int]$_.Exception.Response.StatusCode } catch { }
        }
    } catch {
        $res.error = $_.Exception.Message
    }
    return $res
}

# В выкатке заголовок обязателен: публикуемая сборка его всегда пишет (Write-BuildMarker +
# BuildIdProvider), и «204 без заголовка» означает, что на порту отвечает КТО-ТО ДРУГОЙ —
# старый инстанс под внешним Runner, чужой слушатель или кеширующий прокси, — пока новая
# сборка крутится в цикле падений. Ровно этот отказ заголовок и заведён ловить.
# -AllowMissingBuild — поблажка для отката: снимок старого релиза маркера может не нести.
function Wait-Health([string]$expectedBuild, [int]$timeoutSec, [int]$needSuccess, [switch]$AllowMissingBuild) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $streak = 0
    $last = ''
    while ((Get-Date) -lt $deadline) {
        $r = Test-HealthOnce $HealthUrl
        if ($r.ok) {
            $buildOk = $true
            if ($expectedBuild) {
                if ($r.build) {
                    # Чужой X-Build = отвечает ещё старый процесс. Именно ради этого случая
                    # заголовок и заведён: файл на диске лежит независимо от того, какой exe жив.
                    $buildOk = ($r.build -eq $expectedBuild)
                    if (-not $buildOk) { $last = "health ok, но X-Build='$($r.build)' вместо '$expectedBuild'" }
                } elseif ($AllowMissingBuild -and -not $RequireBuildHeader) {
                    $last = 'health ok, заголовка X-Build нет — снимок релиза его не несёт, засчитываю'
                } else {
                    $buildOk = $false
                    $last = 'health ok, но заголовка X-Build нет: отвечает не наша сборка (чужой слушатель или старый процесс)'
                }
            }
            if ($buildOk) {
                $streak++
                if ($streak -ge $needSuccess) {
                    return [pscustomobject]@{ ok = $true; message = "health $needSuccess/$needSuccess, X-Build='$($r.build)'" }
                }
            } else {
                $streak = 0
            }
        } else {
            $streak = 0
            if ($r.code -gt 0) { $last = "health вернул $($r.code)" } else { $last = "health недоступен: $($r.error)" }
        }
        Start-Sleep -Seconds 2
    }
    if (-not $last) { $last = 'health не ответил ни разу' }
    return [pscustomobject]@{ ok = $false; message = $last }
}

# --- Релизы -------------------------------------------------------------------------------
function Get-ReleaseDirs {
    if (-not (Test-Path $ReleasesDir)) { return @() }
    return @(Get-ChildItem -LiteralPath $ReleasesDir -Directory -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending)
}

# Маркер «какая именно сборка сейчас лежит рядом с exe». Формат держит бэкенд
# (BuildIdProvider): ПЕРВАЯ строка — идентификатор, он уезжает в заголовок X-Build,
# остальные строки свободные и читаются только человеком да этим скриптом.
# Снимок релиза уносит маркер с собой, поэтому после отката заголовок сам становится прежним.
function Get-BuildMarker([string]$dir) {
    $p = Join-Path $dir 'build-id.txt'
    if (-not (Test-Path $p)) { return $null }
    try {
        $lines = @([IO.File]::ReadAllLines($p))
        if ($lines.Count -eq 0) { return $null }
        $marker = [pscustomobject]@{ deployId = $lines[0].Trim(); sha = ''; ref = '' }
        foreach ($line in $lines) {
            if ($line -match '^\s*sha\s*=\s*(.+)$') { $marker.sha = $Matches[1].Trim() }
            if ($line -match '^\s*ref\s*=\s*(.+)$') { $marker.ref = $Matches[1].Trim() }
        }
        return $marker
    } catch { return $null }
}

function Write-BuildMarker([string]$dir, $deployId, $sha, $refName, $dirty) {
    $lines = @(
        "$deployId",
        "sha=$sha",
        "ref=$refName",
        "dirty=$([bool]$dirty)",
        "builtAt=$((Get-Date).ToUniversalTime().ToString('o'))"
    )
    [IO.File]::WriteAllLines((Join-Path $dir 'build-id.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
}

# Возврат снимка релиза: один и тот же порядок и для отката по требованию, и для
# автоотката после провала гейта. Вынесено в функцию не ради красоты — два независимых
# списка шагов неминуемо разъезжаются, а второй путь проверить руками почти невозможно.
function Restore-Release([string]$dir, [string]$name) {
    # Маркер читаем из СНИМКА и ДО копирования. Copy-BuildTree корень не зеркалит (без /MIR),
    # поэтому build-id.txt провалившейся выкатки из публикации сам не исчезнет: прочитанный
    # после копирования, он дал бы гейту сравнить чужое значение само с собой. Снимок без
    # маркера (публикация до появления build-id.txt) — убираем маркер и из PublishDir:
    # отсутствие заголовка честнее, чем враньё о том, какой код сейчас на проде.
    $marker = Get-BuildMarker $dir
    $expect = ''
    if ($marker) { $expect = "$($marker.deployId)" }

    $h = Add-DeployStep 'rollback-stop'
    Stop-ServerStack
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'rollback-restore'
    Copy-BuildTree $dir $PublishDir
    if (-not $marker) {
        Remove-Item -LiteralPath (Join-Path $PublishDir 'build-id.txt') -Force -ErrorAction SilentlyContinue
    }
    Complete-DeployStep $h 'ok' $name

    $h = Add-DeployStep 'rollback-start'
    Start-ServerStack
    Complete-DeployStep $h 'ok' ''

    Set-DeployPhase 'verifying'
    $h = Add-DeployStep 'rollback-health'
    # Ожидаемый X-Build — из маркера снимка: после отката заголовок обязан стать прежним.
    $hz = Wait-Health $expect $HealthTimeoutSec $HealthSuccesses -AllowMissingBuild
    if ($hz.ok) { Complete-DeployStep $h 'ok' $hz.message }
    else { Complete-DeployStep $h 'failed' $hz.message }
    return $hz
}

function Remove-OldReleases([int]$keep) {
    $dirs = Get-ReleaseDirs
    if ($dirs.Count -le $keep) { return @() }
    $removed = @()
    foreach ($d in $dirs[$keep..($dirs.Count - 1)]) {
        try {
            Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Stop
            $removed += $d.Name
        } catch {
            Write-Warn "не удалось убрать старый релиз $($d.Name): $($_.Exception.Message)"
        }
    }
    if ($removed.Count -gt 0 -and $script:Journal) {
        $script:Journal.releases = @((ConvertTo-List $script:Journal.releases) | Where-Object { $removed -notcontains $_.id })
        Write-Journal
    }
    return $removed
}

# --- Старт --------------------------------------------------------------------------------
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$mode = 'deploy'
if ($Rollback) { $mode = 'rollback' }

Write-Head "=== Агент выкатки ($mode) — $PublishDir, env $Environment, порт $Port ==="
Write-Info "репа: $RepoDir" 'DarkGray'
Write-Info "staging: $StagingDir · релизы: $ReleasesDir · журнал: $script:JournalPath" 'DarkGray'
if ($DryRun) { Write-Warn 'СУХОЙ ПРОГОН: журнал не пишется, ни один файл не меняется' }

# Лог на диск: агента запускает планировщик, консоль никто не увидит.
if (-not $DryRun) {
    try {
        $logDir = Join-Path $AgentDir 'logs'
        New-Item -ItemType Directory -Force $logDir -ErrorAction Stop | Out-Null
        Start-Transcript -Path (Join-Path $logDir "$mode-$stamp.log") -Force | Out-Null
        $script:TranscriptOn = $true
        Get-ChildItem -LiteralPath $logDir -Filter '*.log' | Sort-Object Name -Descending |
            Select-Object -Skip 30 | Remove-Item -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warn "транскрипт не пишется: $($_.Exception.Message)"
    }
}

$exitCode = 0
try {
    # --- Guard: один деплой за раз ---
    if (-not (Enter-DeployMutex)) {
        # Единственный отказ ДО чтения журнала: заявку не подхватывали и закрывать нечего —
        # ею занят работающий прямо сейчас первый агент.
        Write-Bad 'ОТКАЗ: выкатка уже идёт (мьютекс Global\ccs-deploy занят)'
        Exit-Agent 2
    }

    # --- Заявка из журнала --------------------------------------------------------------
    # Задача планировщика запускается с фиксированной командной строкой (schtasks /run не
    # умеет передавать аргументы), поэтому опции конкретной выкатки приезжают заявкой в
    # журнале: бэкенд пишет current с phase=queued, агент её подхватывает.
    $script:Journal = Get-Journal
    $adopted = $null
    if ($script:Journal.current -and -not $script:Journal.current.result) {
        if ("$($script:Journal.current.phase)" -eq 'queued') {
            $adopted = $script:Journal.current
        } else {
            # Незакрытая выкатка без живого агента (мьютекс-то мы взяли) — её оборвали.
            Write-Warn "в журнале брошенная выкатка $($script:Journal.current.id) (фаза $($script:Journal.current.phase)) — закрываю как failed"
            $script:Journal.current.phase = 'failed'
            $script:Journal.current.result = [pscustomobject]@{
                ok = $false
                status = 'failed'
                message = 'агент прерван, выкатка не завершена'
                releaseId = ''
                finishedAt = (Get-Date).ToUniversalTime().ToString('o')
            }
        }
    }
    if ($script:Journal.current -and -not $adopted) {
        # Предел истории тот же, что у бэкенда (DeployState.HistoryLimit): журнал читают глазами.
        $script:Journal.history = @(@($script:Journal.current) + (ConvertTo-List $script:Journal.history) | Select-Object -First 30)
    }

    $deployId = $stamp
    $initiatedBy = $null
    $startedAt = (Get-Date).ToUniversalTime().ToString('o')
    if ($adopted) {
        $deployId = "$($adopted.id)"
        if (-not $deployId) { $deployId = $stamp }
        $initiatedBy = $adopted.initiatedBy
        if ("$($adopted.kind)") { $mode = "$($adopted.kind)" }
        if ("$($adopted.startedAt)") { $startedAt = "$($adopted.startedAt)" }
        if ("$($adopted.ref)" -and -not $Ref) { $Ref = "$($adopted.ref)" }
        $rq = $adopted.request
        if ($rq) {
            if ("$($rq.ref)" -and -not $Ref) { $Ref = "$($rq.ref)" }
            if ($rq.skipFrontend) { $SkipFrontend = $true }
            if ($rq.skipSandbox)  { $SkipSandbox = $true }
            if ($rq.allowDirty)   { $AllowDirty = $true }
            if ("$($rq.releaseId)" -and -not $ReleaseId) { $ReleaseId = "$($rq.releaseId)" }
        }
        Write-Info "подхвачена заявка $deployId (kind=$mode)" 'DarkGray'
        # С этой секунды заявка наша: любой выход обязан закрыть её результатом (Exit-Guard),
        # иначе сервер до ручной правки журнала будет считать выкатку идущей.
        $script:Current = $adopted
    }

    # --- Guard: git ----------------------------------------------------------------------
    $git = [pscustomobject]@{ ok = $true; sha = ''; branch = ''; dirty = $false; files = @(); names = @(); error = '' }
    if ($mode -eq 'deploy') {
        if (-not (Test-Tool 'git')) {
            Write-Bad 'ОТКАЗ: git не найден в PATH'
            Exit-Guard 'git не найден в PATH — выкатка не начиналась'
        }
        $git = Get-GitState
        if (-not $git.ok) {
            Write-Bad "ОТКАЗ: $($git.error)"
            Exit-Guard "git не отработал: $($git.error). Выкатка не начиналась"
        }
        Write-Info "ветка $($git.branch), HEAD $($git.sha), файлов с правками: $($git.files.Count)" 'DarkGray'
        if ($Ref -and $Ref -ne $git.branch) {
            Write-Bad "ОТКАЗ: запрошен ref '$Ref', а в рабочем дереве ветка '$($git.branch)'"
            Write-Warn '  Первая волна не переключает ветки: checkout при живом проде — отдельная работа.'
            Write-Warn '  Переключи ветку руками и повтори выкатку.'
            Exit-Guard "запрошен ref '$Ref', а в рабочем дереве ветка '$($git.branch)' — переключи ветку руками и повтори"
        }
        if ($git.dirty -and -not $AllowDirty) {
            Write-Bad 'ОТКАЗ: рабочее дерево грязное, а -AllowDirty не задан'
            foreach ($f in ($git.files | Select-Object -First 20)) { Write-Warn "  $f" }
            if ($git.files.Count -gt 20) { Write-Warn "  ... и ещё $($git.files.Count - 20)" }
            Write-Warn '  Закоммить или спрячь правки — либо запусти с -AllowDirty осознанно.'
            Exit-Guard "рабочее дерево грязное ($($git.files.Count) файлов), а allowDirty не задан — выкатка не начиналась"
        }
        if ($git.dirty) { Write-Warn "едем с грязным деревом ($($git.files.Count) файлов) — так попросили" }
    }

    # --- Guard: живой Runner --------------------------------------------------------------
    $runner = @(Get-Process ClaudeServerTray -ErrorAction SilentlyContinue)
    if ($runner.Count -gt 0 -and -not $IgnoreRunner) {
        Write-Bad "ОТКАЗ: запущен ClaudeCodeServerRunner (ClaudeServerTray.exe), PID $($runner.Id -join ', ')"
        Write-Warn '  Он супервизит продукт и поднимет сервер обратно посреди подмены файлов,'
        Write-Warn '  а в конце на порту окажутся два супервизора. Выйди из Runner и повтори.'
        Exit-Guard "запущен ClaudeCodeServerRunner (PID $($runner.Id -join ', ')) — выйди из Runner и повтори выкатку"
    }
    if ($runner.Count -gt 0) { Write-Warn 'Runner жив, но задан -IgnoreRunner: сервер после подмены поднимет он' }

    # --- Guard: свободное место -----------------------------------------------------------
    $binSize = Get-DirSizeBytes $PublishDir $script:DataDirs
    if ($binSize -le 0) { $binSize = [int64](1GB) }
    $needed = ($binSize * 2) + [int64](1GB)   # staging + новый снимок релиза + запас
    $free = Get-FreeSpaceBytes $PublishDir
    $mb = 1MB
    if ($free -ge 0) {
        Write-Info ("бинарники {0:N0} МБ, нужно ~{1:N0} МБ, свободно {2:N0} МБ" -f ($binSize / $mb), ($needed / $mb), ($free / $mb)) 'DarkGray'
        if ($free -lt $needed) {
            Write-Bad 'ОТКАЗ: на диске не хватит места под staging и снимок релиза'
            Write-Warn '  Освободи место или уменьши KeepReleases — на середине копирования это чинить дороже.'
            Exit-Guard ("на диске не хватит места: нужно ~{0:N0} МБ, свободно {1:N0} МБ — выкатка не начиналась" -f ($needed / $mb), ($free / $mb))
        }
    } else {
        Write-Warn 'свободное место определить не удалось — проверка пропущена'
    }

    # --- Guard: инструменты ---------------------------------------------------------------
    if ($mode -eq 'deploy') {
        $missing = @()
        if (-not (Test-Tool 'dotnet')) { $missing += 'dotnet' }
        if (-not $SkipFrontend -and -not (Test-Tool 'npm')) { $missing += 'npm' }
        if (-not $SkipSandbox -and -not (Test-Tool 'docker')) { $missing += 'docker' }
        if (-not (Test-Tool 'robocopy')) { $missing += 'robocopy' }
        if ($missing.Count -gt 0) {
            Write-Bad "ОТКАЗ: нет инструментов в PATH: $($missing -join ', ')"
            Exit-Guard "нет инструментов в PATH: $($missing -join ', ') — выкатка не начиналась"
        }
    }

    # --- Сухой прогон: план и выход -------------------------------------------------------
    if ($DryRun) {
        Write-Host ''
        Write-Head 'ПЛАН'
        $n = 0
        if ($script:NeedSelfCopy) {
            Write-Host "  0. ФАЗА 0: копия агента в $AgentDir, дальше работает она"
        }
        if ($mode -eq 'rollback') {
            $target = $ReleaseId
            if (-not $target) {
                $dirs = Get-ReleaseDirs
                if ($dirs.Count -gt 0) { $target = $dirs[0].Name } else { $target = '(снимков нет — откат невозможен)' }
            }
            $n++; Write-Host "  $n. фаза switching: остановить трей и сервер"
            $n++; Write-Host "  $n. вернуть снимок релиза $target поверх $PublishDir"
            $n++; Write-Host "  $n. запустить трей"
            $n++; Write-Host "  $n. фаза verifying: health $HealthSuccesses успешных ответа за $HealthTimeoutSec с ($HealthUrl)"
        } else {
            Write-Host '  ФАЗА 1 (сервер жив):'
            if ($SkipFrontend) { $n++; Write-Host "  $n. фронт пропущен (-SkipFrontend)" }
            else { $n++; Write-Host "  $n. npm run build:quiet в $RepoDir\frontend" }
            $n++; Write-Host "  $n. очистить $StagingDir"
            $n++; Write-Host "  $n. dotnet publish ClaudeHomeServer -> staging"
            $n++; Write-Host "  $n. dotnet publish ConPtyBridge -> staging"
            $n++; Write-Host "  $n. dotnet publish ClaudeHomeServer.Tray -> staging + tray.json (env $Environment, url $AppUrl, порт $Port)"
            $n++; Write-Host "  $n. фронт dist -> staging\wwwroot"
            $n++; Write-Host "  $n. MCP-серверы ($($script:McpServers.Count) шт.) + mcp-dify -> staging"
            $n++; Write-Host "  $n. workflow-скрипты -> $env:USERPROFILE\.claude\workflows"
            if ($SkipSandbox) { $n++; Write-Host "  $n. песочница пропущена (-SkipSandbox)" }
            else { $n++; Write-Host "  $n. docker build --target sandbox -t claude-sandbox" }
            Write-Host '  ФАЗА 2 (окно недоступности):'
            $n++; Write-Host "  $n. ClaudeHomeServer.exe --backup (снимок данных)"
            $n++; Write-Host "  $n. стоп трея, сервера, ConPtyBridge"
            $n++; Write-Host "  $n. снимок бинарников -> $ReleasesDir\$stamp (без $($script:DataDirs -join ', '))"
            $n++; Write-Host "  $n. staging -> $PublishDir + build-id.txt (deployId $deployId)"
            if (-not $SkipSandbox) { $n++; Write-Host "  $n. пересоздать контейнер песочницы" }
            $n++; Write-Host "  $n. старт трея"
            Write-Host '  ФАЗА 3 (гейт):'
            $n++; Write-Host "  $n. health $HealthSuccesses успешных ответа за $HealthTimeoutSec с ($HealthUrl), X-Build = $deployId"
            $n++; Write-Host "  $n. не сошлось -> автооткат на $ReleasesDir\$stamp"
            $n++; Write-Host "  $n. ротация снимков, оставить $KeepReleases"
        }
        Write-Host ''
        Write-Good 'Проверки пройдены, изменений не вносилось (-DryRun).'
        Exit-Agent 0
    }

    # --- Журнал: заводим текущую выкатку --------------------------------------------------
    # Набор полей — ровно DeployRecord бэкенда (kind, а не mode; dirtyFiles всегда массив):
    # сервер читает файл в типизированную модель, а при следующей заявке переписывает его
    # целиком — незнакомые ему поля из журнала пропадут.
    $script:Current = [pscustomobject]@{
        id          = $deployId
        kind        = $mode
        phase       = 'queued'
        ref         = $git.branch
        sha         = $git.sha
        dirty       = [bool]$git.dirty
        dirtyFiles  = @($git.names | Select-Object -First 50)
        request     = [pscustomobject]@{
            ref          = $git.branch
            skipFrontend = [bool]$SkipFrontend
            skipSandbox  = [bool]$SkipSandbox
            allowDirty   = [bool]$AllowDirty
            releaseId    = $ReleaseId
        }
        initiatedBy = $initiatedBy
        steps       = @()
        result      = $null
        reported    = $false
        startedAt   = $startedAt
    }
    $script:Journal.current = $script:Current
    Write-Journal

    # ======================================================================================
    # РЕЖИМ ОТКАТА ПО ТРЕБОВАНИЮ
    # ======================================================================================
    if ($mode -eq 'rollback') {
        $dirs = Get-ReleaseDirs
        if ($dirs.Count -eq 0) { throw 'снимков релизов нет — откатывать не на что' }
        $targetDir = $null
        if ($ReleaseId) {
            $targetDir = $dirs | Where-Object { $_.Name -eq $ReleaseId } | Select-Object -First 1
            if (-not $targetDir) { throw "снимок релиза '$ReleaseId' не найден в $ReleasesDir" }
        } else {
            $targetDir = $dirs[0]
        }
        Write-Info "цель отката: $($targetDir.FullName)" 'DarkGray'

        Set-DeployPhase 'switching'
        $hz = Restore-Release $targetDir.FullName $targetDir.Name
        if ($hz.ok) {
            Complete-Deploy 'succeeded' "откат на релиз $($targetDir.Name) выполнен, прод жив" $targetDir.Name
            $exitCode = 0
        } else {
            Complete-Deploy 'failed' "откат на $($targetDir.Name) выполнен, но прод не поднялся: $($hz.message). Нужен человек." $targetDir.Name
            $exitCode = 1
        }
        Exit-Agent $exitCode
    }

    # ======================================================================================
    # ФАЗА 1 — сборка при живом сервере
    # ======================================================================================
    Set-DeployPhase 'building'
    $frontendDir = Join-Path $RepoDir 'frontend'

    $h = Add-DeployStep 'frontend'
    if ($SkipFrontend) {
        Complete-DeployStep $h 'skipped' '-SkipFrontend'
    } else {
        Push-Location $frontendDir
        if (-not (Test-Path 'node_modules')) { npm ci }
        # build:quiet = vite build --logLevel warn: без простыни ассетов в логе агента.
        npm run build:quiet
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0) { Complete-DeployStep $h 'failed' "npm exit $code"; throw "сборка фронта упала (exit $code)" }
        Complete-DeployStep $h 'ok' ''
    }

    $h = Add-DeployStep 'staging-clean'
    if (Test-Path $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Force $StagingDir -ErrorAction Stop | Out-Null
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'publish-backend'
    dotnet publish (Join-Path $RepoDir 'backend\ClaudeHomeServer\ClaudeHomeServer.csproj') -c Release -o $StagingDir
    if ($LASTEXITCODE -ne 0) { Complete-DeployStep $h 'failed' "dotnet exit $LASTEXITCODE"; throw "публикация бэка упала (exit $LASTEXITCODE)" }
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'publish-conpty'
    dotnet publish (Join-Path $RepoDir 'backend\ConPtyBridge\ConPtyBridge.csproj') -c Release -o $StagingDir
    if ($LASTEXITCODE -ne 0) { Complete-DeployStep $h 'failed' "dotnet exit $LASTEXITCODE"; throw "публикация ConPtyBridge упала (exit $LASTEXITCODE)" }
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'publish-tray'
    dotnet publish (Join-Path $RepoDir 'backend\ClaudeHomeServer.Tray\ClaudeHomeServer.Tray.csproj') -c Release -o $StagingDir
    if ($LASTEXITCODE -ne 0) { Complete-DeployStep $h 'failed' "dotnet exit $LASTEXITCODE"; throw "публикация трея упала (exit $LASTEXITCODE)" }
    $trayCfg = [ordered]@{ ServerExe = 'ClaudeHomeServer.exe'; Environment = $Environment; Url = $AppUrl; Port = $Port }
    ($trayCfg | ConvertTo-Json) | Set-Content -Path (Join-Path $StagingDir 'tray.json') -Encoding UTF8
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'frontend-copy'
    $distDir = Join-Path $frontendDir 'dist'
    if (-not (Test-Path (Join-Path $distDir 'index.html'))) {
        Complete-DeployStep $h 'failed' 'нет frontend\dist\index.html'
        throw 'фронт не собран: нет frontend\dist\index.html (прогони без -SkipFrontend)'
    }
    Invoke-Robocopy $distDir (Join-Path $StagingDir 'wwwroot') -Mirror | Out-Null
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'mcp'
    foreach ($srv in $script:McpServers) {
        Invoke-Robocopy (Join-Path $RepoDir "mcp\$srv") (Join-Path $StagingDir "mcp\$srv") -Mirror | Out-Null
    }
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'mcp-dify'
    # mcp-dify (TypeScript, dist собран заранее): DockerPathMapper мапит {PublishDir}/mcp-dify
    # -> /app/mcp-dify, без него dify в песочнице молча пропускается.
    $difySrc = Join-Path $RepoDir 'mcp-dify'
    if (Test-Path (Join-Path $difySrc 'dist\index.js')) {
        $difyDst = Join-Path $StagingDir 'mcp-dify'
        New-Item -ItemType Directory -Force $difyDst -ErrorAction Stop | Out-Null
        foreach ($item in 'dist', 'node_modules') {
            if (Test-Path (Join-Path $difySrc $item)) {
                Invoke-Robocopy (Join-Path $difySrc $item) (Join-Path $difyDst $item) -Mirror | Out-Null
            }
        }
        Copy-Item (Join-Path $difySrc 'package.json') $difyDst -Force -ErrorAction SilentlyContinue
        Complete-DeployStep $h 'ok' ''
    } else {
        # ВАЖНО: mcp-dify мы НЕ зеркалим при подмене, если его нет в staging (см. Copy-BuildTree),
        # так что уже опубликованный на проде остаётся жив.
        Complete-DeployStep $h 'skipped' 'нет dist/index.js — собери mcp-dify перед выкаткой'
    }

    $h = Add-DeployStep 'workflows'
    # Скрипты механик «Обсудить с командой» запускаются по имени из профиля CLI; без них
    # плитка в UI задизейблена. CRLF снимаем: на \r CLI отвечает «control characters».
    $wfDst = Join-Path $env:USERPROFILE '.claude\workflows'
    New-Item -ItemType Directory -Force $wfDst -ErrorAction Stop | Out-Null
    $wfCount = 0
    foreach ($wf in Get-ChildItem (Join-Path $RepoDir 'claude-defaults\workflows\*.js') -ErrorAction SilentlyContinue) {
        $lf = (Get-Content $wf.FullName -Raw) -replace "`r`n", "`n"
        $dst = Join-Path $wfDst $wf.Name
        if (-not (Test-Path $dst) -or [IO.File]::ReadAllText($dst) -ne $lf) {
            [IO.File]::WriteAllText($dst, $lf)
            $wfCount++
        }
    }
    Complete-DeployStep $h 'ok' "обновлено $wfCount"

    $h = Add-DeployStep 'sandbox-image'
    if ($SkipSandbox) {
        Complete-DeployStep $h 'skipped' '-SkipSandbox'
    } else {
        # Образ несёт в себе код MCP-серверов, run-turn.sh и claude CLI: без пересборки ходы
        # container-юзеров исполняются старым кодом. Контейнер пересоздаём в фазе 2 — сносить
        # его сейчас значило бы оборвать идущие ходы за минуты до окна переключения.
        $dockerfile = Join-Path $RepoDir 'backend\ClaudeHomeServer\Dockerfile'
        docker build --target sandbox -t claude-sandbox -f $dockerfile $RepoDir
        if ($LASTEXITCODE -ne 0) { Complete-DeployStep $h 'failed' "docker exit $LASTEXITCODE"; throw "сборка образа песочницы упала (exit $LASTEXITCODE)" }
        Complete-DeployStep $h 'ok' ''
    }

} catch {
    # Сюда попадают все провалы ФАЗЫ 1 и guard'ов после старта журнала: сервер жив,
    # публикацию мы не трогали — просто честно закрываем выкатку.
    $msg = $_.Exception.Message
    if ($mode -eq 'rollback') {
        Write-Bad "Откат не выполнен: $msg"
        # Честно: откат мог оборваться уже ПОСЛЕ остановки сервера — обещать целый прод нельзя.
        Write-Warn 'Проверь состояние прода руками: процессы трея/сервера и содержимое папки публикации.'
    } else {
        Write-Bad "ФАЗА 1 не прошла: $msg"
        Write-Info 'Прод не тронут: подмена бинарников не начиналась.' 'Green'
    }
    Complete-Deploy 'failed' $msg ''
    Exit-Agent 1
}

# ==========================================================================================
# ФАЗЫ 2 и 3 — окно переключения и гейт. Отсюда любой провал ведёт в автооткат.
# ==========================================================================================
$releaseDir = Join-Path $ReleasesDir $stamp
$snapshotTaken = $false
$stopAttempted = $false     # с этой точки прод может лежать — путь назад обязателен
try {
    Set-DeployPhase 'switching'

    $h = Add-DeployStep 'data-backup'
    $note = Invoke-DataBackup
    Complete-DeployStep $h 'ok' $note

    $h = Add-DeployStep 'stop'
    $stopAttempted = $true
    Stop-ServerStack
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'snapshot'
    if (Test-Path (Join-Path $PublishDir 'ClaudeHomeServer.exe')) {
        New-Item -ItemType Directory -Force $releaseDir -ErrorAction Stop | Out-Null
        Invoke-Robocopy $PublishDir $releaseDir -excludeDirs $script:DataDirs | Out-Null
        $snapshotTaken = $true
        $prev = Get-BuildMarker $releaseDir
        $prevSha = ''
        if ($prev) { $prevSha = "$($prev.sha)" }
        $script:Journal.releases = @(@([pscustomobject]@{
            id        = $stamp
            sha       = $prevSha
            path      = $releaseDir
            createdAt = (Get-Date).ToUniversalTime().ToString('o')
        }) + (ConvertTo-List $script:Journal.releases))
        Write-Journal
        Complete-DeployStep $h 'ok' $releaseDir
    } else {
        # Первый деплой в пустую папку: откатываться некуда, и это нормально — но сказать надо.
        Complete-DeployStep $h 'skipped' 'первый деплой: прошлых бинарников нет'
    }

    $h = Add-DeployStep 'swap'
    New-Item -ItemType Directory -Force $PublishDir -ErrorAction Stop | Out-Null
    Copy-BuildTree $StagingDir $PublishDir
    Write-BuildMarker $PublishDir $deployId $git.sha $git.branch $git.dirty
    # appsettings.{Environment}.json — машинно-специфичный и gitignored; при первом деплое
    # берём Production.json, иначе Kestrel уедет на дефолтный порт 5000.
    $envCfg = Join-Path $PublishDir "appsettings.$Environment.json"
    if (-not (Test-Path $envCfg)) {
        Copy-Item (Join-Path $RepoDir 'backend\ClaudeHomeServer\appsettings.Production.json') $envCfg -ErrorAction SilentlyContinue
    }
    Complete-DeployStep $h 'ok' ''

    $h = Add-DeployStep 'sandbox-container'
    if ($SkipSandbox) {
        Complete-DeployStep $h 'skipped' '-SkipSandbox'
    } else {
        # Имя из прод-конфига (Sandbox:ContainerName), дефолт cc-sandbox. Бэкенд поднял бы
        # свежий контейнер и сам, но явное удаление гарантирует переход на новый образ сразу.
        $containerName = 'cc-sandbox'
        $localCfg = Join-Path $PublishDir 'appsettings.Local.json'
        if (Test-Path $localCfg) {
            try {
                $cn = (Get-Content $localCfg -Raw | ConvertFrom-Json).Sandbox.ContainerName
                if ($cn) { $containerName = $cn }
            } catch { }
        }
        docker rm -f $containerName 2>$null | Out-Null
        Complete-DeployStep $h 'ok' $containerName
    }

    $h = Add-DeployStep 'start'
    Start-ServerStack
    Complete-DeployStep $h 'ok' ''

    Set-DeployPhase 'verifying'
    $h = Add-DeployStep 'health'
    $hz = Wait-Health $deployId $HealthTimeoutSec $HealthSuccesses
    if (-not $hz.ok) {
        Complete-DeployStep $h 'failed' $hz.message
        throw "health-гейт не сошёлся: $($hz.message)"
    }
    Complete-DeployStep $h 'ok' $hz.message

    $removed = Remove-OldReleases $KeepReleases
    if ($removed.Count -gt 0) { Write-Info "ротация снимков: убраны $($removed -join ', ')" 'DarkGray' }

    Complete-Deploy 'succeeded' "выкатка $deployId (sha $($git.sha)) прошла, прод отвечает" $deployId
    $exitCode = 0

} catch {
    # --- АВТООТКАТ ------------------------------------------------------------------------
    $reason = $_.Exception.Message
    Write-Bad "ФАЗА переключения провалилась: $reason"
    if (-not $snapshotTaken) {
        # Снимка нет — значит упали на стопе или на самом снимке, а подмена бинарников ещё не
        # начиналась: в PublishDir лежит прежняя, рабочая сборка. Откатывать нечего, но и
        # оставлять прод лежать нельзя — его достаточно поднять. Инвариант ADR-010: из каждой
        # точки после стопа есть путь назад.
        if (-not $stopAttempted) {
            Complete-Deploy 'failed' "$reason. Прод не тронут: сервер жив, публикация не менялась." ''
            Exit-Agent 1
        }
        Write-Warn 'снимка нет, публикация не менялась — поднимаю прод на прежней сборке'
        $revived = $false
        try {
            # Добиваем недобитое перед стартом: два трея на одном порту — вторая авария поверх первой.
            try { Stop-ServerStack } catch { Write-Warn "стоп перед подъёмом не дочистил: $($_.Exception.Message)" }
            Start-ServerStack
            # X-Build не требуем и не сверяем: на диске СТАРАЯ сборка, наш deployId она не отдаст.
            $hz = Wait-Health '' $HealthTimeoutSec $HealthSuccesses
            if ($hz.ok) { $revived = $true } else { Write-Bad "прод не ответил: $($hz.message)" }
        } catch {
            Write-Bad "поднять прод не удалось: $($_.Exception.Message)"
        }
        if ($revived) {
            Write-Good 'прод поднят на прежней сборке'
            Complete-Deploy 'failed' "$reason. Публикация не менялась, прод поднят на ПРЕЖНЕЙ сборке и отвечает." ''
        } else {
            Complete-Deploy 'failed' "$reason. Публикация не менялась, но прод НЕ ПОДНЯЛСЯ — нужен человек: $PublishDir." ''
        }
        Exit-Agent 1
    }
    Write-Warn "возвращаю релиз $stamp"
    $rolled = $false
    try {
        $hz = Restore-Release $releaseDir $stamp
        if ($hz.ok) { $rolled = $true }
    } catch {
        Write-Bad "откат сам упал: $($_.Exception.Message)"
    }
    if ($rolled) {
        Complete-Deploy 'rolled_back' "$reason. Прошлый релиз $stamp возвращён, прод отвечает." $stamp
        $exitCode = 3
    } else {
        Complete-Deploy 'failed' "$reason. Откат не поднял прод — нужен человек: $releaseDir" $stamp
        $exitCode = 1
    }
}

Exit-Agent $exitCode
