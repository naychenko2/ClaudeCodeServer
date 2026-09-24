# Пульт удалённых команд: проект решения

Статус: **на одобрении** (Григорий). Реализация не начата.
Задача трекера: `15e48500-7e29-44ef-84ff-268a31ea4167`. План прошёл ревью
`plan-reviewer` (2026-09-18), замечания вшиты.

## 1. Что строим

Глобальный (не проектный) «пульт» управления машинными процессами на хосте сервера из
веб-морды. Точка входа — меню аватара, рядом с «Питанием компьютера». Первый сценарий —
туннель VS Code (`code tunnel`), дальше — любые заранее объявленные действия (Dify,
Proxifier, службы Windows).

Зафиксированные решения (не пересматриваются):
- пульт глобальный, вход из меню аватара;
- реестр действий — **только** секция в `appsettings.Local.json` (вне git). Из веб-морды
  команды не заводятся и не редактируются никогда: морда торчит наружу, поле произвольной
  команды = RCE.

Почему не панель «Сервисы»: `DevServerService` требует, чтобы процесс начал слушать TCP-порт
(до 30 с), иначе `error` + kill; сервисы привязаны к `projectId` и iframe-превью
`/preview/**`. `code tunnel` порта не слушает, проекта не имеет. Переиспользуем механики:
`OutputRingBuffer` (Core, уже общий у терминалов и дев-серверов) и форму shell-запуска из
`WatchdogCommandRunner` (см. §6).

## 2. Разведка `code tunnel` (проверено на этой машине, VS Code CLI 1.138.0)

- `code tunnel service install|uninstall|log` — **доступно на Windows** (Preview). На этой
  машине служба уже установлена (`service_installed: true`); на Windows это автозапуск от
  логина пользователя, а не SCM-служба — `sc query` её не видит, статус только через CLI.
- `code tunnel status` — oneshot, exit 0, JSON вида
  `{"tunnel":{"tunnel":"Connected",...},"service_installed":true}`. Важно: **exit 0 не
  означает «запущен»** — когда туннеля нет, команда тоже выходит нулём с `"tunnel":null`.
  Отсюда потребность в матчере по выводу (см. `StatusRunningPattern`).
- `code tunnel kill` / `code tunnel restart` — oneshot-команды остановки/перезапуска
  запущенного демона.
- Первичный вход (device-code логин VS Code) интерактивен — из пульта его не пройти.
  Установка и логин делаются один раз руками; пульт управляет уже настроенным туннелем.

Вывод: сценарий туннеля полностью покрывается **oneshot-режимом** — все команды короткие,
демона держит система, а не CCS. Это и есть основной, самый безопасный режим пульта.

## 3. Форма конфига

Секция `RemoteCommands` в `appsettings.Local.json` (опции — `RemoteCommandsOptions` в
`ClaudeHomeServer.Core/Models`, по образцу `PowerControlOptions`). Читается через
`IOptions` — **правка конфига требует рестарта сервиса** (hot-reload не заводим: пульт —
редко меняемый список, а перевалидация на лету — код впрок).

```json
{
  "RemoteCommands": {
    "Enabled": false,
    "Actions": [
      {
        "Key": "vscode-tunnel",
        "Title": "Туннель VS Code",
        "Mode": "oneshot",
        "Start": "code tunnel service install --accept-server-license-terms",
        "Stop": "code tunnel kill",
        "Status": "code tunnel status",
        "StatusRunningPattern": "\"tunnel\":\"Connected\"",
        "TimeoutSeconds": 30
      },
      {
        "Key": "dify",
        "Title": "База знаний (Dify)",
        "Mode": "oneshot",
        "WorkingDir": "C:\\Soft\\dify\\docker",
        "Start": "docker compose up -d",
        "Stop": "docker compose stop",
        "Status": "docker compose ps --services --filter status=running",
        "StatusRunningPattern": "api",
        "TimeoutSeconds": 180
      },
      {
        "Key": "spooler",
        "Title": "Служба печати (пример службы Windows)",
        "Mode": "oneshot",
        "Start": "sc start Spooler",
        "Stop": "sc stop Spooler",
        "Status": "sc query Spooler",
        "StatusRunningPattern": "RUNNING",
        "TimeoutSeconds": 30
      },
      {
        "Key": "proxifier",
        "Title": "Proxifier",
        "Mode": "daemon",
        "Start": "\"C:\\Program Files (x86)\\Proxifier\\Proxifier.exe\"",
        "Stop": "taskkill /im Proxifier.exe",
        "Status": "tasklist /fi \"imagename eq Proxifier.exe\" /nh",
        "StatusRunningPattern": "Proxifier.exe",
        "TimeoutSeconds": 15
      }
    ]
  }
}
```

Запись службы Windows — через `sc start`/`sc stop`/`sc query` (нативные exe, работают под
`cmd`); `Start-Service`/`Stop-Service` — командлеты PowerShell и под объявленным шеллом не
существуют, в комментарии примера об этом предупредить (нужен PowerShell — оборачивать в
`powershell -NoProfile -Command "..."`, помня про вложенные кавычки). Там же две оговорки:
управление службами требует **элевированного** процесса CCS (неэлевированный `sc stop` даст
«Отказано в доступе» с ненулевым exit — по правилам §5 это прочтётся как `stopped`, поэтому
`Status`-команда обязана возвращать ненулевой код только по смыслу «не запущен»); нативные
утилиты Windows (`sc`, `tasklist`) печатают в OEM (cp866) — кириллица в выводе может
приехать битой, `StatusRunningPattern` держать ASCII (в примерах так и есть).

Поля действия:

| Поле | Обязательное | Смысл |
|---|---|---|
| `Key` | да | slug `[a-z0-9-]{1,64}`, уникален; единственное, что ходит по HTTP |
| `Title` | да | подпись в UI |
| `Mode` | нет (`oneshot`) | `oneshot` — все три команды короткие, состояние держит система (службы, docker, планировщик); `daemon` — `Start` порождает долгоживущий процесс, которым владеет CCS |
| `Start` / `Stop` / `Status` | `Start` — да | строки, исполняются через системный шелл (см. §6: Windows — `cmd.exe /s /c` сырой строкой, Unix — `bash -lc`). `Status` обязателен для `oneshot`; для `daemon` опционален (без него после рестарта CCS состояние честно `unknown`) |
| `StatusRunningPattern` | нет | подстрока в stdout `Status`; задана — «запущен» = exit 0 **и** вхождение подстроки; нет — «запущен» = exit 0. Подстрока, не regex: выразительности хватает, а сюрпризов меньше |
| `WorkingDir` | нет | рабочая папка команд; пусто — домашняя папка пользователя сервера |
| `TimeoutSeconds` | нет (30) | потолок ожидания **завершающихся** команд: в oneshot — всех трёх, в daemon — только `Stop` и `Status`. По истечении — kill дерева команды, состояние `unknown` |

Семантика `daemon` (чтобы исполнитель не гадал):
- `Start` **не ждёт выхода** — спавн, затем grace 2 с; процесс жив спустя grace → `ok:true`,
  сдох → `ok:false` + хвост вывода (как `ErrorTail` у дев-серверов). `TimeoutSeconds` на
  `Start` не действует.
- `Stop` при живом child: сначала `Stop`-команда, если задана (штатная остановка вежливее
  kill), ожидание выхода child ≤ `TimeoutSeconds`; не вышел (или `Stop` не задана) —
  `Process.Kill(entireProcessTree: true)` + `WaitForExit(5с)`. При мёртвом/чужом child —
  только `Stop`-команда (ей же добивается сирота прошлой жизни CCS).
- Вывод child пишется в `OutputRingBuffer` действия.

Одна форма покрывает оба мира: «служба Windows» и любой системный демон — oneshot;
«процесс, которым владеет CCS» — daemon.

Валидация при старте: запись с пустым `Key`/`Title`/`Start`, кривым slug, дублем `Key` или
oneshot без `Status` отбрасывается с WARN (тексты команд в лог не пишутся — только ключ и
причина); сервис живёт на оставшихся. Пустой список действий при `Enabled: true` — фича
считается выключенной (пункта меню нет).

## 4. Замки (по образцу `Services/Power`)

1. `[Authorize(Roles = "admin")]` на контроллере.
2. Рубильник `RemoteCommands:Enabled` (false по умолчанию), живёт в машинном
   `appsettings.Local.json` вне git. Как у Power/TrayDeploy: файл читают и боевой, и
   дев-инстанс, а машина одна — включать только там, где пульт действительно нужен
   (комментарий в `appsettings.Local.example.json`).
3. Платформенного замка **нет — осознанно**, в отличие от Power. У Power платформа зашита в
   код (`shutdown.exe`, powrprof.dll), и на Linux команду отдать нечем. У пульта команды
   объявляет хозяин машины под свою ОС; сервер лишь резолвит шелл (`cmd.exe` / `bash`),
   оба существуют на своих платформах всегда.

Семантика выключенного рубильника — ровно как у Power: `GET`-статус отвечает **200 всегда**
(`{"enabled": false, "actions": []}`) — его зовёт шапка при монтировании, и 404 шумел
бы ошибкой в консоли; все мутирующие эндпоинты при выключенном рубильнике — **404**
(«здесь ничего нет» честнее, чем «вам нельзя», когда дело не в правах).

## 5. Живой статус

Источник правды — **результат `Status`-команды, не память процесса**. После рестарта CCS
пульт обязан честно показать состояние: oneshot это даёт даром, daemon — через опциональный
`Status` (нет команды — честный `unknown`).

- `state ∈ {running, stopped, unknown}` + `busy` (идёт операция) + `checkedAt`.
- **`stopped` ≠ «проверить не удалось»** (тот же принцип, что у ревизии каталога MCP:
  404/таймаут — «проверить не удалось», а не «отозван»). Правила:
  `running` — exit 0 (+паттерн, если задан); `stopped` — команда отработала с ненулевым
  exit ЛИБО exit 0 без совпадения паттерна; `unknown` — команду не удалось запустить,
  таймаут с kill, exit 9009 на Windows («не является командой» — опечатка в конфиге, а не
  остановленный демон). К `unknown` всегда идёт `detail` с причиной.
- Сервис держит **кэш последнего результата**; `GET`-список отдаёт кэш и **не исполняет ни
  одной команды** — иначе каждое монтирование шапки гоняло бы N процессов (само по себе
  DoS-поверхность). Свежий статус — явным `refresh` (модалка зовёт его при открытии).
- Для daemon живость собственного child — быстрый положительный сигнал между проверками, но
  при наличии `Status`-команды финальное слово за ней.
- После `start`/`stop` сервис сам прогоняет `Status` и возвращает итоговое состояние в
  ответе — фронту не нужен второй запрос.

## 6. Границы вертикали

- **Код живёт в Main: `backend/ClaudeHomeServer/Services/RemoteCommands/`** — по свежему
  прецеденту Power (сентябрь 2026): микро-вертикаль без `IAppSubsystem`, регистрации в
  `Program.cs` рядом с Power (~строка 862). Курс Этапа 5 (ADR-014) гонит в отдельные
  `.csproj` **накопленные** вертикали; для новой микро-вертикали из трёх файлов отдельная
  сборка — накладные расходы без выигрыша, а сторожа границ охраняют её и в Main. Вырастет —
  вынесем по общей процедуре (потолок «1–2 новых шва» для новой подсистемы соблюдён: новых
  межвертикальных швов ноль).
- **Запись в boundary-каталоге нужна**: строка в `SubsystemBoundaryTests.Boundaries` по
  образцу Power (allow-list = `SharedAllowedPrefixes` + свой namespace, точные допуски
  пустые). `SubsystemBoundaryCoverageTests` подхватит её автоматически через
  `boundaryRoots` — отдельной записи там не нужно (проверено по устройству теста; дубль
  запрещён — он молча схлопывается в `ToHashSet`, ловится только расхождением числа тестов).
- **`IProcessLauncher` не используем — локальный `Process`**. Обоснование:
  `IProcessLauncher`/`ILauncherFactory.ForOwner` — это per-user изоляция среды исполнения
  (local/container) для процессов, порождаемых от имени пользователя, с маппингом путей в
  песочницу. Пульт по определению управляет **хостом сервера** (админ-фича, как Power):
  запускать `sc stop` в контейнере бессмысленно, а зависимость на `Services.Execution`
  из новой вертикали — лишнее ребро для сторожа границ. Правило CLAUDE.md «системные
  one-shot — всегда local» указывает туда же. Kill дерева — `Process.Kill(true)` из BCL.
- **Existence-first: готовый раннер есть, но взять его нельзя, а его фиксы — обязательны.**
  `WatchdogCommandRunner` (`ClaudeHomeServer.Watchdog/WatchdogRunner.cs`) делает почти то
  же (строка + workDir + таймаут + kill + exit/stdout), но работает через per-owner
  `IProcessLauncher` (сторожа исполняются в среде владельца, вплоть до контейнера) — пульту
  нужна противоположность (всегда хост), и прямая ссылка «вертикаль → вертикаль» запрещена
  сторожем границ. Поэтому у пульта свой локальный раннер, куда **обязаны** переехать два
  боевых фикса из Watchdog:
  1. Windows-шелл — `cmd.exe` с аргументами **сырой строкой** `/s /c "<команда>"`, не
     `ArgumentList`: .NET экранирует внутренние `"` как `\"`, cmd этих правил не знает,
     команда с вложенными кавычками разваливалась и давала ложный exit 0 (прод 01.09,
     сторожа `dd1fac4e`/`8ea8c9cc`/`3a091224`). Обе daemon-записи примера §3 — ровно этот
     класс команд. Сборку строки поднять в Core-хелпер
     (`Core/Services/ShellCommandLine.cs`, перенос `WindowsCmdArguments`) и переключить
     Watchdog на него — одна строка call-site, дубль формулы исчезает.
  2. `StandardOutput/ErrorEncoding = UTF8` — без явной кодировки .NET читает OEM/ANSI и
     кириллица в выводе (тот же `sc query` русской Windows) превращается в кракозябры,
     ломая `StatusRunningPattern`.
  Unix-ветка — `bash -lc` (как у Watchdog: логин-шелл ради PATH; на целевой Windows-машине
  не используется, но CI — Linux, и тесты раннера гоняются именно там).
- **Шов для тестов**: интерфейс `IShellCommandRunner` (запустить строку в шелле, вернуть
  exit/stdout/stderr, наблюдать таймаут) — точка подмены, как `IPowerActions`; исполнять
  реальные команды в тестах нельзя.
- **Жизненный цикл daemon-детей**: сервис регистрируется и как `IHostedService` — в
  `StopAsync` штатной остановки CCS гасит своих детей (kill дерева). Иначе сирота
  появлялся бы при **каждом** рестарте (в т.ч. выкатке), а не только при аварии; после
  аварийной смерти CCS сироту добивает `Stop`-команда (риск №2).

## 7. REST-контракт

База: `api/admin/remote-commands`, контроллер под `[Authorize(Roles = "admin")]`.

| Метод | Путь | Ответы |
|---|---|---|
| GET | `/api/admin/remote-commands` | 200 всегда: `{ enabled, actions: [{ key, title, mode, state, busy, checkedAt, lastExitCode }] }` (`checkedAt`/`lastExitCode` — null до первой проверки); выключено — `{ enabled: false, actions: [] }`. Тексты команд наружу **не отдаются** |
| POST | `/api/admin/remote-commands/{key}/start` | 404 — рубильник выключен или ключ неизвестен; 409 — по действию уже идёт операция; 200 — `{ ok, state, detail? }` (`detail` — короткая причина + до 15 последних строк вывода при провале) |
| POST | `/api/admin/remote-commands/{key}/stop` | те же |
| POST | `/api/admin/remote-commands/{key}/refresh` | 404 по тем же правилам; **409 не отвечает**: при идущей операции не берёт gate, а возвращает кэш + `busy: true` — второе окно и модалка во время долгого старта видят живое «занято», а не конфликт |
| GET | `/api/admin/remote-commands/{key}/output` | 200 `{ text }` — реплей `OutputRingBuffer` действия (буфер per-action, дефолт 200 000 символов, копится через запуски до вытеснения); 404 по тем же правилам |

Операции синхронные в пределах `TimeoutSeconds` — HTTP-запрос ждёт итог (как у большинства
админ-действий; у долгих действий вроде Dify фронту нужен свой запас на fetch).

**Событие обновления статуса — не заводим.** Единственный потребитель статуса — открытая
модалка пульта: итог операции приходит ответом её же запроса, прогресс чужой операции —
мягким поллингом `refresh` при открытой модалке (см. выше: во время `busy` он дёшев — кэш
без исполнения команд). Появится дашборд статусов на главной — тогда
`remote_commands_changed` по образцу `watchdogs_changed` через
`ISessionBroadcaster.ToOwner` каждому админу; закладываться сейчас — код впрок.

## 8. Фронт (кратко)

- `HubHeader.tsx` — замок и загрузка статуса по образцу Power: `api.remoteCommands.status()`
  зовётся **только при `isAdmin`** (иначе 403 шумит в консоли), проп в меню передаётся при
  `enabled && actions.length > 0`; там же монтируется модалка.
- `AvatarMenu.tsx` — пункт «Пульт управления» под «Питанием компьютера»; серверная проверка
  всё равно на каждом вызове (морда торчит наружу — прятать в UI без серверного замка
  нельзя).
- `RemoteCommandsModal.tsx` по образцу `PowerModal.tsx`: список действий, бейдж состояния
  (`running`/`stopped`/`unknown`/спиннер `busy`), кнопки «Запустить»/«Остановить»/«Обновить»,
  разворот хвоста вывода; при открытии — `refresh` по каждому действию, при `busy` — мягкий
  поллинг. Токены `C.*`, контролы из `components/ui/`, мобильная вёрстка.
- `lib/api.ts` — типы и вызовы рядом с power-блоком.

## 9. Инварианты

1. Команды приходят **только из конфиг-файла на диске**; по HTTP ездит только `key`.
   Никогда не принимать команду, аргументы или рабочую папку из запроса.
2. Источник правды о состоянии — `Status`-команда, не память процесса; неизвестно — честный
   `unknown` с причиной, а не догадка (и не «остановлен»).
3. `GET`-список не исполняет команд и не отдаёт их тексты.
4. Выключенный рубильник: статус-эндпоинт 200 `{enabled:false}`, мутирующие — 404
   (модель Power).
5. На одно действие — не больше одной операции одновременно (`busy` → 409 у start/stop);
   действия независимы друг от друга.
6. В серверные логи не попадают ни тексты команд, ни их вывод; попадают ключ, операция,
   **имя пользователя** (аудит «кто нажал», как у Power), exit code и длительность. Вывод
   живёт только в памяти (`OutputRingBuffer`) и отдаётся только админу.
7. Смерть/рестарт CCS не оставляет вранья: кэш статуса пуст до первой проверки, daemon без
   `Status`-команды показывает `unknown`. Штатная остановка CCS гасит daemon-детей.

## 10. Риски

1. **`stop` не убил процесс** (oneshot вернул 0, а демон жив) — пульт не соврёт: после stop
   прогоняется `Status`, и карточка покажет `running`. Для daemon: порядок из §3
   (`Stop`-команда → kill дерева → `WaitForExit(5с)`); не умер — `ok:false`, `unknown`.
2. **Осиротевший daemon после аварийной смерти CCS** — child прошлой жизни ничей; без
   `Stop`/`Status`-команд его не видно и не убить. Лечение — рекомендация в комментарии
   примера: для daemon задавать `Stop`/`Status` внешними командами (`taskkill`/`tasklist`
   по имени); stop при мёртвом child исполняет `Stop`-команду, что добивает сироту.
   Штатные рестарты закрыты `IHostedService.StopAsync` (§6).
3. **Одновременные нажатия** (два окна, даблклик) — per-action gate, второй start/stop
   получает 409 с причиной; UI дизейблит кнопки по `busy`; `refresh` вне gate.
4. **Зависшая команда** — kill дерева по `TimeoutSeconds`. Опасный случай — таймаут посреди
   «долгого старта» (`docker compose up` качает образы): kill оставит полусостояние.
   Смягчение: таймаут per-action (у Dify в примере 180 с), состояние после — `unknown`,
   повторный start идемпотентен у типовых команд.
5. **Секреты**: команда может нести токен в аргументах, вывод — печатать ключи. Закрыто
   инвариантом 6 (не логируем) и тем, что вывод и конфиг видит только админ. Остаточный
   риск: секрет в командной строке виден в списке процессов ОС любому локальному
   пользователю — свойство ОС; в доке конфига советуем секреты класть в env/файлы.
6. **RCE-поверхность** — сведена к выбору из белого списка: исполняется только то, что
   хозяин руками записал в файл на диске. Компрометация admin-сессии даёт запуск/остановку
   объявленных действий, но не произвольные команды.
7. **Перепутан режим** (блокирующий демон объявлен oneshot) — start висит до таймаута, затем
   kill дерева убьёт только что запущенное. Симптом понятный (start «не работает»),
   лечение — `Mode: daemon`; отметить в комментарии примера.
8. **Дев- и боевой инстанс читают один Local.json** — оба управляют одной машиной; двойной
   start типовых команд идемпотентен, но комментарий примера, как у Power, велит включать
   рубильник осмысленно.
9. **`code tunnel service` в статусе Preview** — CLI может поменять вывод/команды; ломается
   только конфиг-запись (правится в Local.json + рестарт сервиса, без релиза продукта) —
   за это и платим конфиг-подходом.
10. **Кавычки cmd** — самый коварный класс сбоев (ложный exit 0 у команды с вложенными
    кавычками); закрыт переносом фиксов Watchdog (§6) и юнит-тестом на сборку командной
    строки.

## 11. План реализации шагами

| # | Шаг | Файлы | Критерий проверки |
|---|---|---|---|
| 1 | Опции: `RemoteCommandsOptions` (`Section`, `Enabled`, `Actions`, дефолты, enum режима) с комментарием-образцом как у `PowerControlOptions` | `backend/ClaudeHomeServer.Core/Models/RemoteCommandsOptions.cs` | `dotnet build` зелёный |
| 2 | Пример конфига: секция §3 + `_remoteCommandsComment` (когда включать, режимы, daemon-рекомендация про `Stop`/`Status`, службы через `sc`, секреты — в env/файлы) | `backend/ClaudeHomeServer/appsettings.Local.example.json` | JSON валиден (парс) |
| 3 | Core-хелпер сборки cmd-строки: перенос `WindowsCmdArguments` (`/s /c` сырой строкой) в `ShellCommandLine` (root-namespace `ClaudeHomeServer.Services`, как `SafePath`); Watchdog переключается на него; строка `"ClaudeHomeServer.Services.ShellCommandLine"` в `CoreAllowedRootTypes` (иначе краснеет `CoreDll_СодержитТолькоРазрешённыеНеймспейсы`); тесты Watchdog-раннера — на новый адрес хелпера | `backend/ClaudeHomeServer.Core/Services/ShellCommandLine.cs`, `backend/ClaudeHomeServer.Watchdog/WatchdogRunner.cs`, `backend/ClaudeHomeServer.Tests/Services/SubsystemBoundaryTests.cs`, `backend/ClaudeHomeServer.Tests/Services/Watchdog/WatchdogCommandRunnerTests.cs` | `dotnet test --filter "FullyQualifiedName~Boundary"` и тесты Watchdog зелёные; юнит-тест хелпера на вложенные кавычки |
| 4 | Раннер: `IShellCommandRunner` + `LocalShellCommandRunner` (Windows `cmd /s /c` raw / Unix `bash -lc`, `WorkingDir`, UTF-8 stdio, таймаут → kill дерева, вывод в `OutputRingBuffer`, exit code) | `backend/ClaudeHomeServer/Services/RemoteCommands/IShellCommandRunner.cs`, `LocalShellCommandRunner.cs` | юнит-тесты на кросс-платформенных командах (пути от `Path.GetTempPath()`, тайминги — через событие, не `Task.Delay`) |
| 5 | Сервис: `RemoteCommandsService` — валидация/отброс кривых записей с WARN, per-action gate, кэш статуса, классификация `running/stopped/unknown` по §5, `StartAsync`/`StopAsync`/`RefreshAsync`, daemon-владение процессом, `IHostedService.StopAsync` гасит детей | `backend/ClaudeHomeServer/Services/RemoteCommands/RemoteCommandsService.cs` | тесты с фейковым раннером: busy-конфликт; refresh без gate; exit≠0 → `stopped`, таймаут/сбой запуска → `unknown`; паттерн-матч; отброс дублей `Key` и oneshot без `Status`; рубильник выключен → отказ |
| 6 | Контроллер: маршруты §7, 404/409/200-семантика, `[Authorize(Roles="admin")]`, лог с именем пользователя | `backend/ClaudeHomeServer/Controllers/RemoteCommandsController.cs` | тесты `WebApplicationFactory`: аноним 401, не-админ 403, выключено → GET 200 `{enabled:false}` + POST 404, неизвестный ключ 404 |
| 7 | Регистрация рядом с Power: `Configure<RemoteCommandsOptions>` + `AddSingleton<RemoteCommandsService>` + `AddSingleton<IHostedService>(sp => sp.GetRequiredService<RemoteCommandsService>())` — именно так, а не `AddHostedService<T>()`: тот создаст ВТОРОЙ инстанс, и `StopAsync` тихо погасит детей у пустышки | `backend/ClaudeHomeServer/Program.cs` (~стр. 862) | `dotnet build`; тест «hosted-инстанс == синглтон» по образцу `SpendSubsystemRegistrationTests.Register_SpendCollector_IsSameInstanceAsStore`; счётчик регистраций в метрике ADR-014 сдвигается осознанно |
| 8 | Boundary-запись по образцу Power | `backend/ClaudeHomeServer.Tests/Services/SubsystemBoundaryTests.cs` | `dotnet test --filter "FullyQualifiedName~Boundary"` зелёный; число тестов выросло ровно на 1 |
| 9 | Фронт: типы+вызовы, замок и загрузка в шапке, пункт меню, модалка | `frontend/src/lib/api.ts`, `frontend/src/components/HubHeader.tsx`, `frontend/src/features/projects/AvatarMenu.tsx`, `frontend/src/components/RemoteCommandsModal.tsx` | `npx tsc -b` и `npm run lint:design` зелёные; перед коммитом UI — предложить прогон субагента `designer` |
| 10 | Документация: раздел в `docs/architecture/features.md` + выжимка со ссылкой в CLAUDE.md | `docs/architecture/features.md`, `CLAUDE.md` | перечитка на полноту (инварианты §9 отражены) |
| 11 | Финальный гейт | — | `dotnet test --filter "Category!=Dns"` полный, `npm run build`; ручная проверка на этой машине: **round-trip туннеля из модалки** — `stop` → статус `stopped` → `start` → статус `running` (`"tunnel":"Connected"`) |
