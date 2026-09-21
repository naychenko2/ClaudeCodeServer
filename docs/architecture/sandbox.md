# Среда исполнения пользователей (local / container)

> Подробная документация подсистемы. Выжимка и инварианты — в [CLAUDE.md](../../CLAUDE.md),
> раздел «Среда исполнения». Читать перед правками в `Services/Execution/`, `SandboxManager`,
> `UserHomeResolver` и всём, что касается запуска процессов пользователей.

Изоляция per-**пользователь**, а не per-приложение: у `User.ExecutionEnvironment`
(`local` | `container`, задаётся админом при создании) два режима. **local** — процессы
пользователя (claude, терминал, dev-серверы, npx skills) запускаются на машине сервера
с полным доступом. **container** — всё исполняется в общей docker-песочнице `cc-sandbox`.
Модель предполагает бэкенд НА ХОСТЕ (Windows), а не в контейнере.

- **Слой запуска** — [Services/Execution/](../../backend/ClaudeHomeServer/Services/Execution/):
  `IProcessLauncher` (`ProcessSpec` → `Process`) с драйверами `LocalProcessRunner`
  (Process.Start, как раньше) и `DockerProcessRunner` (`docker exec -i cc-sandbox
  /app/run-turn.sh <turnId> …`, stdio stream-json насквозь). Резолв по владельцу —
  `ILauncherFactory.ForOwner(ownerId)`. Все 6 точек запуска (ClaudeSession,
  OneShotClaudeRunner, ModelCatalogService, TerminalService, DevServerService,
  SkillsCliService) идут через него; системные one-shot (changelog, каталог моделей) —
  всегда local.
- **Изоляция local-процессов по памяти** (секция `Execution:Isolation`, по умолчанию выключена) —
  после инцидента 2026-09-19 (systemd-oomd дважды убил весь `ccs.service`: сборки агентов жили
  в cgroup прода). На не-Windows `LocalProcessRunner` запускает процесс как
  `systemd-run --user --scope --quiet --collect --slice=<Slice> --property=MemoryHigh=… --property=MemoryMax=… -- <exe> <args…>`:
  scope оказывается вне cgroup `ccs.service` — по умолчанию user-юниты живут в `app.slice`,
  а `ccs.slice`, которому подчинён `ccs-agents.slice`, его сиблинг под `user@<uid>.service`,
  — и при нехватке памяти умирает scope агента, а не прод. PID тот же, поэтому `Kill` и
  interrupt не меняются. Fail-open с одним warning за процесс: нет `systemd-run`,
  нет user-шины, задан `RawArguments`, либо не прошла **проба systemd-run** —
  `systemd-run` есть в PATH, но в рантайме обёртка не работает (нет прав на slice,
  битая user-шина, не тот systemd): без пробы КАЖДЫЙ запуск агента падал и прод стоял.
  Проба выполняется **один раз за процесс** (лениво, результат кэшируется,
  потокобезопасно) и повторяет запуск с теми же флагами, что `WrapperArgs`, — включая
  `--unit=`, которому выдаётся СВЕЖЕЕ имя (`NewScopeUnitName`, не заглушка оценки длины):
  проба обязана быть той же формы, что боевой запуск, а фиксированное имя столкнулось бы
  с живым юнитом соседнего инстанса и выглядело бы как мёртвая обёртка. Итого
  `systemd-run <флаги> -- true` с таймаутом 5 с (шов-делегат, тестам не нужен
  настоящий systemd). Код 0 — обёртка разрешена; ненулевой код / таймаут / исключение
  — процесс запускается напрямую, один warning с причиной.
  Оценка длины командной строки учитывает обёртку.
  `DockerProcessRunner` не затронут.

  **Реюз узлов сборки внутри хода и гашение scope** (`Execution:Isolation:BuildNodeReuse`,
  дефолт true). Scope именуется `--unit=ccs-run-<pid бэкенда в hex>-<guid>.scope`
  (отпечаток владельца — для сторожа сирот, см. ниже; ширина hex постоянна, поэтому
  длина имени не плавает и заглушка оценки командной строки остаётся точной),
  и узлы MSBuild и сервер
  компилятора живут весь ход: соль рукопожатия узлов (`MSBUILDNODEHANDSHAKESALT`) и труба
  компилятора (`SharedCompilationId`) — имя юнита, поэтому параллельные ходы не цепляются к
  узлам друг друга (иначе остановка чужого scope роняла бы идущую сборку). Унаследованные
  запреты реюза снимаются, явный `spec.Env` сильнее. По выходу обёрнутого процесса
  (событие `Exited`, подписка до `Start`) `systemctl --user stop --no-block <unit>` гасит
  оставшийся хвост: `KillMode` scope по умолчанию `control-group`, узлы выходят на SIGTERM
  сразу. Гонки с `--collect` нет — опустевший scope уже собран, и stop отвечает кодом 5
  («not loaded»), это штатный исход. Прочие сбои — один warning на класс, не исключение.
  Шов для тестов — `LocalProcessRunner.StopScope`. Что даёт (замер 2026-09-19, пересборка
  `ClaudeHomeServer.Tests` после правки в Core, `-m:4`): прежний режим 7,3–7,5 с на каждой
  сборке; с реюзом первая сборка хода 7,9 с, следующие 4,0 и 3,3 с. Между ходами ничего не
  переиспользуется — новый scope = холодные узлы. Откат без пересборки — `BuildNodeReuse=false`:
  возвращаются `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
  `UseSharedCompilation=false` (для не-сборщиков безвредны).

  **Сторож scope-сирот при старте** (`ScopeOrphanSweeper.Sweep`, зовётся из `Program.cs`
  фоном сразу после чтения `Execution:Isolation`). Гашение scope висит на событии `Exited`
  и живёт ровно столько, сколько процесс бэкенда: упал бэкенд во время хода или прогрева —
  scope с узлами MSBuild и VBCSCompiler остаётся в slice навсегда (`--collect` не
  срабатывает, из cgroup никто не выходил; `ProcessRegistry.PruneDead` о нём не знает,
  записи нет). Это путь накопления памяти в том самом slice, где нас дважды убивал OOM, —
  и виден он только со следующего старта. Сирота отличается от чужого живого scope тремя
  замками, ни один из которых не таймер: чужой ПОЛЬЗОВАТЕЛЬ невидим по построению
  (`systemctl --user`, шина у каждого своя); чужой ИНСТАНС того же пользователя
  (dev-стенд рядом с продом) — по отпечатку владельца в имени юнита, гасим только
  тех, чей PID мёртв, поэтому живой сосед и scope, заведённый секунду назад параллельным
  стартом, не трогаются; имя не нашего формата не трогается вовсе. Ошибка возможна только
  в безопасную сторону: переиспользованный номер мёртвого владельца делает сироту
  «живой», и она доживёт до следующего старта. Швы для тестов —
  `ScopeOrphanSweeper.ListScopes` / `IsProcessAlive`, гасит общий `LocalProcessRunner.StopScope`;
  настоящий systemd тестам не нужен. Fail-open: сбой перечисления — строка в лог, старт не
  падает.
- **Прогрев сборки свежего worktree** (`WorktreeBuildWarmup`, тумблер `Execution:WarmupBuild`,
  дефолт true, читается на каждом вызове). Холодная сборка свежего дерева — 60–90 с, и без
  прогрева её оплачивает модель. После `SessionManager.SetWorktreeAsync` (дерево заводит
  сервер) и `AttachWorktreeAsync` (дерево задачи заводит человек/агент) фоном запускается
  `dotnet build backend/ClaudeHomeServer.Tests -m:4` в корне дерева через
  `ILauncherFactory.ForOwner(ownerId)` — та же изоляция и пределы памяти, что у ходов.
  Fire-and-forget: ход не ждёт, результат только в лог, таймаут 15 мин. Не более одного
  прогрева на дерево за жизнь процесса и только если тестовый проект есть и ещё не
  собирался (нет `obj/`) — иначе прогрев дрался бы с идущей сборкой агента за obj/bin.
  Прогрев помечен `ProcessSpec.Heavy` и идёт под потолок (см. ниже): слот берётся БЕЗ
  ожидания — свободен, стартуем на месте; занят, ожидание уезжает в фон, а заведение дерева
  возвращается немедленно. Ждём, а не отменяем: дерево живёт долго и сборка всё равно
  понадобится. Условие проверяется ЗАНОВО перед стартом — за время в очереди дерево могли
  начать собирать или снести, и тогда прогрев отменяется (причина разведена в логе).
  Остаточный риск: агент, начавший `dotnet build` в первые секунды хода свежего дерева,
  соберёт параллельно с прогревом.
- **Потолок одновременных тяжёлых запусков** (`BuildConcurrencyGate`,
  `Execution:Isolation:MaxConcurrentBuilds`, дефолт 2; явный `0` — без ограничения, откат
  без пересборки). Инцидент 2026-09-21: `systemd-oomd` третий раз за три дня убил прод —
  пределы памяти заданы **per-scope**, а число одновременных scope не ограничивало ничто, и
  `ccs-agents.slice` держал 18,1 GB в четырёх параллельных прогонах. Счётчик **единственный
  на процесс** (`Instance`, ставится из `Program.cs` через `Configure`): второй семафор
  где-то ещё делает потолок неправдой. Под него идут только spec с явной меткой
  `ProcessSpec.Heavy` — сегодня это ровно прогрев; `git status`/`diff` (их сотни), проба MCP
  и one-shot claude проходят насквозь, очередь из них парализовала бы git-бар. Сама метка
  ничего не ограничивает: слот берёт ВЫЗЫВАЮЩИЙ перед запуском, потому что
  `LocalProcessRunner.Start` синхронен и ждать слот внутри него значило бы заблокировать
  вызывающий поток. **Потолок не зависит от `Isolation:Enabled`** (дефолт которого `false`):
  он про то, сколько сборок идёт разом, а не в каком cgroup они живут, — выключение секции
  изоляции его НЕ снимает, это делает только свой ключ. Чего потолок не видит осознанно:
  сборку, которую агент запускает ВНУТРИ хода своим Bash, — она потомок процесса claude CLI,
  а не отдельный запуск. Отсюда же отсутствие дедлоков: ход слот не занимает и не ждёт его
  никогда, поэтому «ход ждёт слот, занятый прогревом того же дерева» невозможно по
  конструкции. Цена в памяти на слот (замер 2026-09-21, `dotnet build ClaudeHomeServer.Tests
  -m:4` в своём scope, пик `MemoryCurrent`): ~640 MiB на прогон.
- **Пути** — `IPathMapper`: бэкенд ВСЕГДА работает с хостовыми путями (projects.json
  хранит `C:\…`), а процессы container-юзера — с контейнерными; перевод в момент
  запуска (`DockerPathMapper`, аналог SafeJoin — путь вне монтирований → ошибка).
  Точки монтирования: `Sandbox:ProjectsRoot`→`/projects`, `data/sandbox-profiles`→
  `/sandbox-profiles` (per-user CLAUDE_CONFIG_DIR + транскрипты resume, видны бэкенду
  через `WorkflowAgentParser.AddAllowedRoot`), `data/sandbox-tmp`→`/turn-tmp`
  (MCP-конфиги хода, one-shot cwd).
- **Interrupt** — `run-turn.sh` пишет pgid хода в `/tmp/turns/{turnId}.pid`;
  `DockerProcessRunner.Kill` добивает группу изнутри (`kill -KILL -- -pgid`), т.к.
  убийство docker-клиента на хосте не трогает процесс в контейнере.
- **MCP из песочницы** — `*_API_URL` = `Sandbox:McpApiUrl` (`host.docker.internal:5000`,
  Kestrel хоста) через `ResolveTasksApiUrl(ownerId)`; node-серверы `mcp/*/index.js`
  лежат в образе под `/app/mcp` (переписываются в `BuildTurnMcpConfig`).
- **Токен подписки (CLAUDE_CODE_OAUTH_TOKEN) — доставка per-exec.** Одна точка правды —
  `DockerProcessRunner.BuildTurnEnv`: в конец env каждого `docker exec` докладывается
  primary-токен из окружения бэкенда, если ход его ещё не несёт (`CLAUDE_CODE_OAUTH_TOKEN`,
  `ANTHROPIC_AUTH_TOKEN` или `ANTHROPIC_API_KEY` уже в env — например, ход аккаунта пула или
  стороннего провайдера — фолбэк пропускается, инвариант «токен подписки не уезжает чужому
  эндпоинту» держится). `docker run` (создание/пересоздание контейнера в `SandboxManager`)
  токен больше НЕ передаёт — раньше он запекался в момент создания и не обновлялся, поэтому
  контейнер, поднятый до появления токена в окружении бэкенда, жил без него до пересоздания.
  `EnsureRunningAsync` логирует warning в ветке создания контейнера, если токена в
  окружении бэкенда нет.
- **Миграция транскриптов (смена провайдера, фейловер пула, worktree).** Транскрипт CLI —
  локальный файл `{профиль}/projects/{уплощённый cwd}/{csid}.jsonl`, поэтому «переезд» чата
  между провайдерами и рабочими папками — это копирование файла (`TranscriptMigrator`).
  У container-юзера обе координаты другие, и обе считает `SessionManager`:
  - **профиль** — `ConfigRootFor(ownerId, providerKey)`: хостовый корень
    (`ConfigRootForProvider`) переводится в песочный `{data/sandbox-profiles}/{ownerId}/{ключ}`.
    Ключ выводится из ИМЕНИ ПАПКИ хостового корня, а `~/.claude` (профиль без оверрайда) →
    `default` — ровно то же правило, что в `DockerProcessRunner.RewriteProfileEnv`, которое
    задаёт `CLAUDE_CONFIG_DIR` живого хода. **Зеркало обязано сходиться**: расчёт ключа
    из `providerKey` («primary → default») разошёлся бы с ходом, потому что
    `ConfigRootForProvider("claude")` отдаёт `sub-claude`, когда запись `claude` задана в
    пуле подписок с токеном. Сторожат два теста в `SessionManagerContainerMigrationTests`.
  - **cwd** — `CwdForOwner(ownerId, hostCwd)` через `IPathMapper.ToRuntime`: CLI внутри
    контейнера видит `/projects/…` и уплощает именно этот путь (`-projects-…`), а бэкенд
    хранит хостовый `C:\…`. Путь вне монтирований `ToRuntime` отвергает: у явных операций
    (`MigrateProviderAsync`, `SetWorktreeAsync`) причина уезжает пользователю (400), у
    авто-фейловера пула — тихая деградация с логом, как и любой другой отказ переноса.
    В `SetWorktreeAsync` перевод обеих папок считается ДО `WorktreeAddAsync`/
    `WorktreeRemoveAsync` — иначе исключение маппинга обошло бы rollback и оставило
    дерево-сироту.

  Уборка при удалении чата (`DeleteTranscript` → `DeleteEverywhere`) ключ не считает вовсе:
  она берёт ВСЕ папки `{sandbox-profiles}/{ownerId}/*` и полагается на фолбэк-скан, потому
  что приходящий cwd хостовый, а копии после переездов остаются намеренно.

  ⚠️ **Egress-proxy.** Если `Sandbox:Proxy` настроен с whitelist только Anthropic-доменов,
  чат, мигрированный на СТОРОННИЙ эндпоинт, перестанет работать: сама миграция пройдёт
  (это копирование файлов на хосте), а первый же ход упадёт сетевой ошибкой из контейнера.
  Pre-flight проверки эндпоинта намеренно нет — лечится расширением whitelist прокси.
- **Корни проектов разведены**: local-юзеры — `DefaultProjectsPath`, container-юзеры —
  `Sandbox:ProjectsRoot` (в песочницу монтируется только он). Единая точка резолва —
  [UserHomeResolver.cs](../../backend/ClaudeHomeServer/Services/UserHomeResolver.cs): домашняя
  папка юзера = `{база по среде}/{логин}`, внутри неё живут проекты без явного пути, `Chats`
  и корни файловых триггеров. Все четыре потребителя (`ProjectManager.Create`,
  `SessionManager.ResolveChatRoot`, `PersonaAgentFileSync.ChatRoot`, `AutomationRootResolver`)
  ходят через него.
  **Override**: `Projects:UserHomeOverrides` (словарь логин → абсолютный путь, в
  appsettings.Local.json) снимает прослойку `{логин}` — на однопользовательском инстансе
  можно работать прямо в общей папке (`"admin": "C:\\GIT"`). Путь обязан быть абсолютным, а у
  container-юзеров — лежать СТРОГО внутри `Sandbox:ProjectsRoot` (сам корень общий для всех
  изолированных, домом одного быть не может); негодный override игнорируется с warning. Уже
  созданные проекты не затрагиваются (`RootPath` абсолютный), а у чатов вне проекта меняется
  cwd — старые такие чаты остаются в прежней папке и могут потерять `--resume`.
  Существующую папку в проект подключают без всего этого: `POST /api/projects` с явным
  `rootPath` (на фронте — «Добавить проект» → «Существующий»).
- **Guard**: смена `ExecutionEnvironment` при существующих чатах запрещена (разные корни
  и профили; `SessionManager.HasSessionsOwnedBy`). **SandboxManager** держит один общий
  контейнер (docker CLI, `sleep infinity`, ленивый `EnsureRunningAsync`, пересоздание при
  смене образа/параметров по label-хешу). Конфиг — секция `Sandbox` (машинно-специфичный
  `ProjectsRoot` — в appsettings.Local.json). Образ песочницы:
  `docker build --target sandbox -t claude-sandbox -f backend/ClaudeHomeServer/Dockerfile .`
- **Переход на per-user контейнеры** позже без переделки: имя контейнера параметризовано,
  меняется только `SandboxManager`/фабрика драйвера.
