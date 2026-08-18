# scripts/ops — операционные скрипты

Две категории:

- **`deploy-agent.ps1`** — постоянный механизм: агент выкатки прода (ADR-010).
- **разовые починки данных** (`fix-*.ps1`) — каждая с сухим прогоном по умолчанию,
  собственным бэкапом и записью в этом файле: что чинилось, когда и почему.

---

## deploy-agent.ps1 — агент выкатки прода

Исполняет выкатку целиком и переживает смерть сервера. Решение и обоснование —
[ADR-010](../../docs/adr/ADR-010-deploy-from-chat.md); шаги повторяют `deploy80.ps1`,
но в другом порядке: сборка идёт при **живом** сервере, переключение — короткое окно.

```
ФАЗА 0  копия агента в C:\deploy\ccs-deploy (скрипт не переписывает сам себя)
ФАЗА 1  guard'ы -> npm build -> dotnet publish в staging -> docker build   (сервер ЖИВ)
ФАЗА 2  exe --backup -> стоп -> снимок релиза -> staging поверх publish -> старт
ФАЗА 3  health 3 раза за 90 с (+ X-Build) -> не сошлось -> автооткат на снимок
```

**Guard'ы** (любой не сошёлся — выход с кодом 2, на диске ничего не изменено): грязное
рабочее дерево без `-AllowDirty`, занятый мьютекс `Global\ccs-deploy`, живой Runner
(`ClaudeServerTray.exe`), нехватка места под staging и снимок, отсутствие `git`/`dotnet`/
`npm`/`docker`/`robocopy` в PATH.

**Коды возврата:** `0` успех · `1` провал · `2` отказ guard'а · `3` выкатка откачена.

### Параметры

| Параметр | Дефолт | Зачем |
|---|---|---|
| `-PublishDir` | `C:\deploy\claude` | куда публикуется прод |
| `-StagingDir` | `C:\deploy\claude.staging` | сборка при живом сервере |
| `-ReleasesDir` | `C:\deploy\claude.releases` | снимки релизов + `deploy-state.json` |
| `-AgentDir` | `C:\deploy\ccs-deploy` | рабочая копия агента и его логи (**вне** PublishDir) |
| `-RepoDir` | от места скрипта | корень репозитория-источника |
| `-Environment` | `Production80` | `ASPNETCORE_ENVIRONMENT` сервера и `tray.json` |
| `-AppUrl` / `-Port` | `https://naychenko.me` / `80` | «Открыть в браузере» в трее и адрес health |
| `-HealthUrl` | `http://127.0.0.1:<Port>/api/health` | адрес гейта |
| `-KeepReleases` | `3` | сколько снимков релизов держим |
| `-HealthTimeoutSec` / `-HealthSuccesses` | `90` / `3` | условие гейта |
| `-SkipFrontend`, `-SkipSandbox`, `-AllowDirty` | — | как в `deploy80.ps1` |
| `-Rollback [-ReleaseId <id>]` | — | вернуть указанный (по умолчанию последний) снимок |
| `-DryRun` | — | guard'ы + план шагов, **ни одного изменения на диске** |
| `-IgnoreRunner` | — | ручной обход guard'а «живой Runner» (из чата не задаётся) |
| `-RequireBuildHeader` | — | отсутствие `X-Build` считать провалом гейта |

### Шов с бэкендом

Стыкуемся ровно двумя файлами — командной строки в этом шве нет вообще.

1. **`<ReleasesDir>\deploy-state.json`** — журнал. Сервер пишет заявку (`current` с
   `phase: "queued"`, `kind`, `request`, `initiatedBy`) и потом `reported`; агент пишет
   фазы, шаги, `result` и список `releases`. Агент подхватывает заявку по `phase=queued`
   и берёт из неё `id`, `kind`, `ref`, `request.*`, `initiatedBy`.

   > `schtasks /run` аргументов не передаёт — **любая опция конкретной выкатки едет
   > журналом**, а не флагом. Флаги командной строки остаются для ручных запусков.

2. **`<PublishDir>\build-id.txt`** — маркер сборки. Первая строка — идентификатор выкатки,
   его сервер отдаёт заголовком `X-Build` у `GET /api/health` (`BuildIdProvider`); дальше
   свободные строки `sha=`, `ref=`, `dirty=`, `builtAt=` для человека. Снимок релиза уносит
   маркер с собой, поэтому после отката заголовок сам становится прежним.

Пока опубликованная сборка заголовка не отдаёт, гейт засчитывает ответ и пишет об этом в
лог. Требовать заголовок жёстко — `-RequireBuildHeader` (включать, когда бэкенд-часть
приедет на прод).

### Разовое заведение задачи планировщика `CCS-Deploy`

**Задача заводится руками один раз. Агент её не создаёт и не правит** — это граница
привилегий: заявка из чата умеет только *запустить* уже существующую задачу
(`schtasks /run /tn CCS-Deploy`).

Задача указывает на скрипт **в репозитории** — фаза 0 сама скопирует его в `AgentDir` и
передаст работу копии, так что обновление агента приезжает обычным `git pull`.

```powershell
# от имени владельца прода, PowerShell в обычном режиме
$repo = 'C:\Sources\ClaudeCodeServer'
$tr = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$repo\scripts\ops\deploy-agent.ps1`""

schtasks /create /tn CCS-Deploy /sc once /st 00:00 /sd 01/01/2030 `
         /tr $tr /rl HIGHEST /f
```

Разбор ключей:

- `/sc once /st 00:00 /sd 01/01/2030` — расписания у задачи фактически нет: она нужна
  только как точка запуска по `/run`. Дата в будущем, чтобы задача не сработала сама.
- `/rl HIGHEST` — мьютекс `Global\…` и остановка процессов требуют полных прав.
- **Без `/ru SYSTEM`.** Агент должен работать под учёткой владельца: он ходит в
  `~\.claude\workflows`, в docker и в профили CLI. SYSTEM видит другой профиль и другой
  docker-контекст.

Проверить и запустить руками:

```powershell
schtasks /query /tn CCS-Deploy /v /fo list | Select-String 'Задача|Task To Run|Запуск|Состояние|Status'
schtasks /run   /tn CCS-Deploy
Get-Content C:\deploy\ccs-deploy\logs\deploy-*.log -Tail 40   # что делает агент прямо сейчас
```

> Задача запускается **под сеансом пользователя**: если владелец не залогинен, поставь
> в свойствах задачи «Выполнять вне зависимости от регистрации пользователя» — тогда
> Windows спросит пароль учётной записи, и это осознанное решение владельца, а не скрипта.

Секция конфига сервера (`appsettings.Local.json`), которую читает `DeployOptions` — пути
обязаны совпадать с параметрами задачи:

```json
"Deploy": {
  "Enabled": true,
  "RepoDir": "C:/Sources/ClaudeCodeServer",
  "AgentDir": "C:/deploy/ccs-deploy",
  "PublishDir": "C:/deploy/claude",
  "StagingDir": "C:/deploy/claude.staging",
  "ReleasesDir": "C:/deploy/claude.releases",
  "KeepReleases": 3,
  "HealthTimeoutSec": 90,
  "TaskName": "CCS-Deploy"
}
```

### Ручные запуски

```powershell
cd C:\Sources\ClaudeCodeServer\scripts\ops
.\deploy-agent.ps1 -DryRun                     # guard'ы и план, ничего не трогает
.\deploy-agent.ps1                             # обычная выкатка
.\deploy-agent.ps1 -SkipFrontend -SkipSandbox  # быстрая, только бэк
.\deploy-agent.ps1 -Rollback                   # вернуть предыдущий релиз
.\deploy-agent.ps1 -Rollback -ReleaseId 20260818-135500
```

### Что агент НЕ делает (и почему)

- **Не переключает ветки.** `-Ref`/`request.ref`, не совпавший с текущей веткой рабочего
  дерева, — отказ. `checkout` при живом проде — отдельная работа со своими граблями
  (незакоммиченные правки, сабмодули, откат ветки при провале).
- **Не трогает ярлык автозапуска** — им заведует `deploy80.ps1`, дублировать нечего.
- **Не откатывает данные.** Снимок `exe --backup` перед выкаткой остаётся, но
  восстановление — явное решение человека через меню трея (ADR-010, «Откат»).
- **Не удаляет `data`, `logs`, `backups`, `certs`** ни при копировании, ни при откате.

---

## ПОЛИГОН: тестовый контур выкатки

**Отлаживать механизм на боевом инстансе нельзя** — половина сценариев это «сервер лёг»:
провал гейта, автооткат, смерть сервера посреди фазы 2. Поэтому у механизма свой контур:
отдельная папка публикации, свой порт, свои данные и своя задача планировщика.

### 1. Развернуть контур

```powershell
$repo = 'C:\Sources\ClaudeCodeServer'

# appsettings.Test80.json для тестового инстанса: свой порт и СВОЙ DataPath.
# DataPath — путь к projects.json, каталог данных берётся от него (см. Program.cs).
$cfg = @{
    Kestrel  = @{ Endpoints = @{ Http = @{ Url = 'http://127.0.0.1:8080' } } }
    DataPath = 'C:/ClaudeData/deploytest/projects.json'
} | ConvertTo-Json -Depth 5
New-Item -ItemType Directory -Force 'C:\deploy\claude-test', 'C:\ClaudeData\deploytest' | Out-Null
Set-Content 'C:\deploy\claude-test\appsettings.Test80.json' $cfg -Encoding UTF8

# первая публикация — обычным агентом, руками
cd $repo\scripts\ops
.\deploy-agent.ps1 -PublishDir C:\deploy\claude-test -StagingDir C:\deploy\claude-test.staging `
                   -ReleasesDir C:\deploy\claude-test.releases -AgentDir C:\deploy\ccs-deploy-test `
                   -Environment Test80 -Port 8080 -AppUrl http://localhost:8080 `
                   -SkipSandbox -AllowDirty
```

> Ключ `-SkipSandbox` на полигоне обязателен по смыслу: образ песочницы и контейнер
> `cc-sandbox` — общие с боевым инстансом, и пересборка/пересоздание из теста ударит по
> проду. Свой `DataPath` — тоже не формальность: без него тестовый инстанс поедет в боевые
> сторы.

### 2. Задача планировщика полигона

```powershell
$repo = 'C:\Sources\ClaudeCodeServer'
$tr = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$repo\scripts\ops\deploy-agent.ps1`"" +
      " -PublishDir C:\deploy\claude-test -StagingDir C:\deploy\claude-test.staging" +
      " -ReleasesDir C:\deploy\claude-test.releases -AgentDir C:\deploy\ccs-deploy-test" +
      " -Environment Test80 -Port 8080 -AppUrl http://localhost:8080 -SkipSandbox"

schtasks /create /tn CCS-Deploy-Test /sc once /st 00:00 /sd 01/01/2030 /tr $tr /rl HIGHEST /f
```

В конфиг тестового инстанса — та же секция `Deploy`, но с путями полигона и
`"TaskName": "CCS-Deploy-Test"`. Боевая задача `CCS-Deploy` и тестовая живут порознь и
никогда не пересекаются по путям.

### 3. Сценарии, которые полигон обязан отработать

Нумерация — критерии проверки ADR-010.

| # | Сценарий | Как воспроизвести | Ожидание |
|---|---|---|---|
| 1 | ошибка компиляции фронта | внести синтаксическую ошибку в любой `.tsx` | `failed`, сервер полигона ни секунды не лежал, публикация не тронута |
| 2 | новая версия падает на старте | в `appsettings.Test80.json` staging'а прописать заведомо занятый порт | гейт не сошёлся → `rolled_back`, `X-Build` = прежний |
| 3 | смерть сервера не убивает агента | во время фазы 2 смотреть `Get-Process powershell \| Select Id,StartTime` и дерево процессов | агент жив, его родитель — `svchost` (планировщик), а не сервер |
| 4 | повторный запуск при идущей выкатке | `schtasks /run /tn CCS-Deploy-Test` дважды подряд | второй выходит с кодом 2 «мьютекс занят», второй агент не работает |
| 5 | грязное дерево | оставить незакоммиченный файл | код 2 со списком файлов; с `-AllowDirty` едет и пишет `sha`+`dirty` в журнал |
| 6 | ручной откат | `.\deploy-agent.ps1 -Rollback -ReleaseId <id> ...пути полигона` | прод полигона поднялся на N-1, `result.releaseId` = снимок |

Проверять после каждого прогона:

```powershell
Get-Content C:\deploy\claude-test.releases\deploy-state.json -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 8
Get-ChildItem C:\deploy\claude-test.releases           # снимки и ротация (KeepReleases)
Get-Content C:\deploy\claude-test\build-id.txt         # какой сборкой сейчас накрыта папка
(Invoke-WebRequest http://127.0.0.1:8080/api/health -UseBasicParsing).Headers['X-Build']
```

### 4. Убрать полигон

```powershell
schtasks /delete /tn CCS-Deploy-Test /f
Remove-Item C:\deploy\claude-test, C:\deploy\claude-test.staging, C:\deploy\claude-test.releases, `
            C:\deploy\ccs-deploy-test, C:\ClaudeData\deploytest -Recurse -Force
```

---

## fix-local-action-route-prefix.ps1

**Что чинит.** Маршрут места применения модели, сохранённый голым именем модели прямого
адаптера — `auto:smart` вместо `direct:auto:smart`. Без префикса значение попадает в форму
«id модели провайдера» ([ADR-009](../../docs/adr/ADR-009-local-action-route-format.md) §1,
форма 8) и уезжает в claude CLI вместо прямого HTTP-адаптера. Место при этом выглядит
настроенным, а фактически каждый вызов падает по таймауту и работает страховкой цепочки.

**Когда обнаружено.** 2026-08-12, прод (`C:\ClaudeData\prod`). Найдено 5 записей:

| Место | Было | Стало |
|---|---|---|
| `chat-retitle` | `auto:fast` | `direct:auto:fast` |
| `team-memory-compress` | `auto:fast` | `direct:auto:fast` |
| `persona-memory-autolearn` | `auto:smart` | `direct:auto:smart` |
| `team-memory-autolearn` | `auto:smart` | `direct:auto:smart` |
| `dossier-summary` | `auto:smart` | `direct:auto:smart` |

Модель и поставщик не меняются — правится только форма записи маршрута.

**Свидетельство дефекта** (`C:\ClaudeServer\prod\logs\server.log`):

```
09:00:07  cheap-runner: действие persona-memory-autolearn — модель auto:smart
          недоступна, иду дальше по цепочке
          LlmTimeoutException at OneShotClaudeRunner.RunCliAsync
```

против места с корректным префиксом (`chat-title` = `direct:auto:fast`):

```
09:26:42  cheap-runner: действие chat-title — прямой вызов auto:fast ...
```

**Критерий «кривизны» берётся из конфига, не хардкодом:** чинится только значение, чья
модель объявлена источником прямого адаптера (`CheapHttpSources:{key}:Models` или
`OpenRouter:DirectModels`) — то есть у которого есть канонический вид `direct:{id}` в
каталоге `/api/models`.

### Чего скрипт НЕ трогает (и почему это правильно)

- `fusion` (18 мест) и `MiniMax-M3` (в шагах пресетов) — модели **CLI-провайдеров**
  (freellmapi, minimax). Для них голое имя каноническое (ADR-009 §1, форма 8). Они тоже
  валятся в лог по таймауту, но это **не формат маршрута**, а медленный CLI-транспорт —
  отдельная задача (для `fusion` — добавить её в `CheapHttpSources:freellmapi:Models`).
- Приписать `direct:` модели CLI-провайдера — **сломать рабочее значение**: `ResolveSource`
  не найдёт такой id ни в одном источнике и уедет в первый настроенный (freellmapi) с чужим
  именем модели.

---

### Порядок применения на проде

Прод (`C:\ClaudeServer\prod\ClaudeHomeServer.exe`) запущен трей-супервизором
`ClaudeHomeServer.Tray.exe`, который **авто-перезапускает сервер через 3 с после падения**.
Поэтому останавливать через `Stop-Process` бесполезно — только через меню трея.

Рестарт не graceful (`Kill` по дереву процессов) — оборвёт идущие чаты, ходы задач и
фоновые действия. Выбирать момент, когда на проде не идёт активная работа.

1. **Сухой прогон** — убедиться, что список правок тот же (скрипт ничего не пишет):

   ```powershell
   cd C:\Sources\ClaudeCodeServer\scripts\ops
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod
   ```

2. **Остановить сервер:** правый клик по иконке в трее → **«Выход (остановить сервер)»**.

3. **Применить правку** (скрипт сам снимет бэкап перед записью):

   ```powershell
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod -Apply
   ```

4. **Запустить сервер:** снова запустить `C:\ClaudeServer\prod\ClaudeHomeServer.Tray.exe`
   (он поднимет бэкенд сам).

> Более короткий вариант без остановки: прогнать шаг 3 на живом сервере и сразу сделать
> в трее **«Перезапустить сервер»**. Работает потому, что стор читает `local-actions.json`
> только при старте. Риск — узкое окно: если в эти секунды кто-то сохранит маршрут в
> разделе «Поставщики моделей», сервер перезапишет файл своим снимком из памяти и правка
> молча потеряется. При остановленном сервере такого окна нет.

**Почему обязателен рестарт.** `LocalActionOverridesStore` читает файл только при старте и
переписывает его целиком из снимка в памяти при любом сохранении из UI. Правка файла без
рестарта не применяется и может быть затёрта.

---

### Бэкап

- **Снят до правок, вручную:**
  `C:\ClaudeData\prod\backups\ops\local-action-route-prefix-20260812-112121\`
  — `local-actions.json` (SHA256 `AF7BFE869D5DC73F0238AD9139158EBE3B5B158E930EC9DCF98C6BD0F8FCCCF0`)
  и `specialty-settings.json` (для контекста, скрипт его не трогает).
- **Скрипт при `-Apply` делает свой бэкап** в
  `<DataPath>\backups\ops\local-action-route-prefix-<timestamp>\`: исходный
  `local-actions.json` + `changes.json` со списком применённых замен. Путь печатается в вывод.

### Откат

1. Остановить сервер через трей («Выход (остановить сервер)»).
2. Вернуть файл из бэкапа:

   ```powershell
   Copy-Item 'C:\ClaudeData\prod\backups\ops\local-action-route-prefix-20260812-112121\local-actions.json' `
             'C:\ClaudeData\prod\local-actions.json' -Force
   ```

3. Запустить `ClaudeHomeServer.Tray.exe`.

### Проверка результата

1. **Повторный сухой прогон** — тем же критерием, которым искали. Должен сказать, что
   чинить нечего:

   ```powershell
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod
   # ожидаемо: «Записей с маршрутом без префикса не найдено — чинить нечего.»
   ```

2. **Глазами по файлу** — голых `auto:*` не осталось, только префиксные:

   ```powershell
   # пусто = хорошо (ищем ":\"auto:" без direct:)
   Select-String -Path C:\ClaudeData\prod\local-actions.json -Pattern '"[^"]+":"auto:[^"]+"'
   # для сравнения — префиксные на месте
   Select-String -Path C:\ClaudeData\prod\local-actions.json -Pattern 'direct:auto:' -AllMatches
   ```

3. **По логу — что сценарий реально отрабатывает.** После рестарта дождаться вызова
   починенного места (`persona-memory-autolearn` дёргается раз в несколько минут) и
   убедиться, что вместо «недоступна» идёт прямой вызов:

   ```powershell
   Select-String -Path C:\ClaudeServer\prod\logs\server.log `
                 -Pattern 'persona-memory-autolearn|team-memory-autolearn' | Select-Object -Last 5
   # было:  «модель auto:smart недоступна, иду дальше по цепочке»
   # стало: «прямой вызов auto:smart …» либо тишина (шаг отработал без warning'а)
   ```
