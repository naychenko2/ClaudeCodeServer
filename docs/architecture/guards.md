# Карта сторожей проекта

Сторож — тест, который держит инвариант и страхует от повторения инцидента: он падает,
когда «правильное» поведение кто-то случайно сломал. Знание о том, что именно держит
каждый и почему, живёт в комментариях тестов, `CLAUDE.md` и ADR; эта карта сводит его в
одном месте. Сторожи лежат в `backend/ClaudeHomeServer.Tests/**` и
`backend/ClaudeHomeServer.Git.Tests/**` (классы `*GuardTests`, `*BoundaryTests`,
`*RegressionTests`, `*StabilityTests`, `*CoverageTests`).

Поля каждого раздела: **файл**, **инвариант**, **источник** (инцидент/ADR/документ),
**как ловит**, **слепые зоны**, **расширение** (что делать при новой сущности).

## Сводная таблица

| Сторож | Инвариант | Источник | Слепые зоны |
|---|---|---|---|
| `GitAccessBoundaryTests` | сырые `RunAsync`/`RunOkAsync` GitService не `public` | комментарий теста (волны 1–3, этап 3) | новые сырые методы кроме двух |
| `DifyToolsetTraversalGuardTests` | `document_id` проходит гейт формы до резолва датасета и HTTP | тест: блокер приёмки волны 4.1 | только Dify-тулзы с `document_id` |
| `PersonaCreateOnboardingGuardTests` | в личном знакомстве `personas_create` запрещён, пока заготовка живая | `onboarding-intro.md` §«Когда приглашение гаснет» | только `POST /api/personas`; `delete` не покрывает |
| `TasksControllerDroppedGuardTests` | снятая штабом задача не затирается агентским PUT без заголовка | `team-implement-mode.md` (волна 1 team-blocker-honest) | только `PUT /api/tasks/{id}`; DELETE/создание вне скоупа |
| `McpInstructionsLengthGuardTests` | `instructions` ответа initialize ≤ 1950 символов | тест: живой проб — CLI режет на ~2 КБ | только personas/tasks-серверы; вне node — skip |
| `CoordinatorWriteGuardTests` | координатор не пишет файлы обходными shell-командами | `team-implement-mode.md` §Э7 (находка Веры) | регэксп — не AST; новые обходы вне списка InlineData |
| `DesktopMcpToolsetStabilityTests` | состав `tools/list` desktop-server постоянен при любом env и состоянии хода | ADR-008 (desktop-agent) | строковый скан; только desktop-server |
| `FalAccumulatorRaceRegressionTests` | стоимость FAL не теряется при оживлении аккумулятора под `_falPersistLock` | тест: ревью волны 2 (Blocker 1) | сценарий воспроизведён руками; один сценарий |
| `HiggsfieldFailureClassRegressionTests` | failure-classification Higgsfield: немой EnsureFresh → NeedsAuth; снимок старше 24 ч → пустой состав | тест: мутационное ревью Глеба (коммит 3aa69833) | один зафиксированный порядок на путь; граница 24 ч — с запасом, не детерминированно |
| `IlBoundaryRegressionTests` | IL-скан сторожей границ видит все 7 известных швов (3 — во вложенных типах) | `docs/research/il-boundary-scan-2026-09.md`, CLAUDE.md «Известное ограничение» | только 7 зафиксированных швов; переименование типа роняет тест |
| `InotifyLeakRegressionTests` | сбой слежки не плодит inotify-экземпляры рекурсией (инцидент 19.09) | `file-watching.md` §«Два вида сбоя» | только Linux; ENAMETOOLONG/EMFILE-сценарии не на реальном ENOSPC |
| `IrreversibleCommandGuardTests` | стоп-список необратимых команд режима «Авто»: каждая форма ловится, повседневные — нет | `features.md` §«Режимы прав» | регэксп, не AST; новый shell-диалект/формат вне таблиц |
| `LocalLlmClientBoundaryTests` | `ILocalLlmClient`/клиенты не упоминаются типами вне `ClaudeHomeServer.Llm` (allow-list: SessionManager) | `llm-providers.md` (потребители; голосовой ход) | IL-скан: string-резолвы/`[FromKeyedServices]` вне скоупа; 1 allow-list |
| `LucideGlyphWhitelistGuardTests` | белый список `LucideGlyphs.All` совпадает с ключами `dynamicIconImports.mjs` установленного lucide-react | ADR-009 §5, ревизия 17.08.2026 | вне node_modules (CI) — skip; дрейф ловит vitest-сторож |
| `McpToolsetStabilityTests` | состав `tools/list` MCP-серверов (ключи + `shapes`) не зависит от свойств ХОДА; мерцание = рестарт claude со ВСЕМИ MCP | `mcp-servers.md` §«Инвариант: состав инструментов не зависит от хода» («наступали трижды»), CLAUDE.md; ADR-008/012/004 — ссылки | скан по именам переменных; без node/вне дерева — skip; живой tools/list только personas/memory/workspace |
| `ProjectMapHygieneGuardTests` | `CLAUDE.md` ≤ 520 строк, без мёртвых ссылок/@-импортов | `research/claude-md-cleanup-2026-09.md` (уборка 1013→477) | качество/длина секций не меряются; «токены» — оценка |
| `RootSubsystemBoundaryTests` | root-синглтоны `Services` (без поддеревьев) не уходят в подсистемные вертикали вне allow-list | тест: ревью 50f3b849 (KnowledgeService + PersonaManager); ADR-014 (спинка) | только top-level `Services`; nested/суб-namespace-типы не сканируются |
| `SecFetchSiteGuardTests` | кука-аутентификация прокси — только same-origin; отсутствие заголовка — не отказ | тест (SameSite=Strict и соседний поддомен) | только Sec-Fetch-Site; не-браузерных клиентов не покрывает (и не обязан) |
| `SsrfGuardTests` | `IsPublic`/`IsPubliclyRoutable` отсекают loopback/приватные/link-local/CGNAT/multicast/NAT64, пропускают публичные | ADR-005 (link-reader-server); задача «SSRF-обход» 2026-08-03 | только классификация адресов; DNS-rebinding и редирект-политика Reader'а вне класса |
| `SubscriptionWindowMismatchGuardTests` | чужой setup-токен: расхождение сброса 5h-окна (oauth vs probe/turn) → ровно один алерт на смену | тест: инцидент 20–23.08.2026; инвариант e862a991 | пара каналов одного ключа; DNS/сеть — моки |
| `SubsystemBoundaryCoverageTests` | полнота таблицы Boundaries: каждая IAppSubsystem-подсистема и каждый Services.*-namespace с типами — в таблице (или явная спинка-исключение) | ADR-014; прецедент: 5 namespaces без строк; мутации 23d353d7, этап 4 (сент. 2026) | «спинка»-исключения — ручной список; nested/compiler-типы игнорируются |
| `SubsystemBoundaryTests` | вертикаль (`Services.<X>`) зависит только от «спины» + объявленных швов (default-deny, allow-list `Boundaries`, 36 записей) | ADR-014 (2026-09-01): этап 1, «Правило зависимостей вертикалей», «Устройство сторожей границ» | string-резолвы/`[FromKeyedServices]`/`Reflection.Emit`; новая сборка без форс-загрузки — вакуум |
| `TeamAsyncAgentStallGuardTests` | гард молчаливого тупика async-агента: 10-минутный потолок подавления; ровно на границе — карточка | тест: баг b63fd8ea (подавление висело бессрочно) | чистая функция; живая метка/процесс не проверяются |
| `TestingDefaultProjectsPathGuardTests` | тестовый хост (appsettings.Testing.json) даёт пустой `DefaultProjectsPath` — проекты не уходят в прод-`/projects` | тест (маппинг `C:\ClaudeHome`) | один факт; поведение UserHomeResolver при пустом пути не проверяется |
| `MetricTagGuardTests` | кардинальность значений тегов метрик: путь/свободный `model` схлопываются в Overflow; потолок РАЗНЫХ значений | тест (clickhouse-retention; AllowedTags стережет только имена) | 2 тег-ключа (tool_name, model); другие измерения вне скоупа |

## Сторожи

> Разделы идут в порядке обработки; порядок не несёт смысла.

### GitAccessBoundaryTests

- **Файл:** `backend/ClaudeHomeServer.Git.Tests/Services/GitAccessBoundaryTests.cs`
- **Инвариант:** `GitService.RunAsync`/`RunOkAsync` (сырые git-команды) не `public` и не
  `protected` — внешний код ходит только через типизированные методы GitService.
- **Источник:** комментарий теста: волны 1–3 и этап 3 (вынос Git-вертикали в отдельный
  `.csproj` `ClaudeHomeServer.Git`); отдельный ADR в репозитории не найден.
- **Как ловит:** reflection над модификатором доступа (`IsPublic || IsFamily`) — два
  `Fact`'а. Компилятор сам молчит, если кто-то добавит нового вызывающего внутри
  `Services/Git/` — сторож закрывает именно эту дыру.
- **Слепые зоны:** проверены ровно два метода; новый сырой метод с другим именем (не
  Run/RunOk) сторож не увидит; обёртки-«подделки» внутри vertical-каталога не отличимы.
- **Расширение:** новый сырой/обходной метод — добавить пару `Fact` по образцу
  `RunAsync_Не_Pубличный`. Сторож живёт в `ClaudeHomeServer.Git.Tests` и видит
  internal через `InternalsVisibleTo` (второй атрибут — на `ClaudeHomeServer.Tests`,
  ради тестов, оставшихся в Main).

### DifyToolsetTraversalGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Controllers/DifyToolsetTraversalGuardTests.cs`
- **Инвариант:** `document_id` во всех Dify-тулзах проходит белый список формы
  (`IsValidDifyId`) ДО резолва датасета и любого HTTP — dot-сегмент не резолвит запрос
  в чужой датасет под общим workspace-ключом.
- **Источник:** тест-комментарий: блокер приёмки волны 4.1 (пейлоад
  `../../{uuid}/documents/{doc}` со своим валидным `dataset_id` читал/писал/удал
  чужие документы); ADR в репозитории не найден (источник — сам тест).
- **Как ловит:** интеграционные тесты через живого хоста (`TestWebApplicationFactory`),
  но Dify подменён записывающим хендлером (без сети). Theory на 5 document-инструментов
  с traversal-пейлоадом (ожидается `isError` + ни одного HTTP), чистые dot-сегменты
  (`..`, `../..`, `.`), позитивный контроль (UUID доезжает до Dify) и лимит имени
  публичной базы: ровно 40 символов проходит без префикса, 41+ — подсказка с планкой 40,
  а не личным бюджетом.
- **Слепые зоны:** покрывает document-инструменты Dify и форму имени `create_dataset`;
  `dataset_id` в других тулзах, `files_document_*` (иная ось, см. ADR-012) и
  `update_document_by_*` в новых итерациях — не покрыты.
- **Расширение:** новый document-тулс Dify с `document_id` — добавить его в
  `InlineData` theory; новый гейт формы — отдельный `Fact` по образцу
  `ГодныйDocumentId_ПроходитГейтИдетВDify`.

### PersonaCreateOnboardingGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Controllers/PersonaCreateOnboardingGuardTests.cs`
- **Инвариант:** в личном знакомстве (`OnboardingKind == "user"`) создание новой персоны
  запрещено, ПОКА заготовка-ассистент резолвится в живую персону — интервью
  дорабатывает её через `personas_update`, а не плодит дубликат.
- **Источник:** `docs/architecture/onboarding-intro.md`, раздел «Когда приглашение
  гаснет» («предохранитель перестал бы держать (модель смогла бы создать вторую
  персону)»); в комментарии теста — «план 2.9, PersonasController.Create».
- **Как ловит:** три HTTP-теста: живой заготовка → `POST /api/personas` даёт 400 с
  подсказкой `personas_update`; мёртвый `AssistantPersonaId` → 200 (деградация,
  знакомство не заперто навсегда); проектное знакомство → 200 (предохранитель не
  трогает штатное создание руководителя).
- **Слепые зоны:** только `POST /api/personas`; `personas_update`/`personas_delete` и
  MCP-путь создания персон вне скоупа.
- **Расширение:** новый путь создания персоны (MCP-тулза, новый endpoint) — добавить
  `Fact` с тем же гейтом; новый вид знакомства — InlineData с новым `OnboardingKind`.

### TasksControllerDroppedGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Controllers/TasksControllerDroppedGuardTests.cs`
- **Инвариант:** снятая человеком (штабом) задача не затирается опоздавшим
  агентским `PUT /api/tasks/{id}`: агентский путь (заголовок `X-Caller-Session-Id`)
  получает внятный 400 «снята человеком», человек из UI (без заголовка) вправе вернуть
  задачу в работу.
- **Источник:** тест-комментарий: фикс-волна 4 `team-blocker-honest`, пункт 5 повторного
  ревью; механика снятия блокера штабом — `docs/architecture/team-implement-mode.md`.
- **Как ловит:** два интеграционных теста на живом хосте: маркер снятия
  (`TaskManager.MarkDroppedByHuman`) + `PUT` с заголовком-сессией → 400, статус не
  затёрт, пометка снятия на месте; `PUT` без заголовка → 200, статус применён, пометка
  снята. Разведение путей — по заголовку: MCP-серверы шлют его на каждый вызов,
  браузер не шлёт никогда (подделка только строгает правило).
- **Слепые зоны:** только `PUT /api/tasks/{id}` (tasks_update); `DELETE`/создание
  задачи и другие методы не покрываются; stdio-ветка `mcp/tasks-server/index.js`
  проверяется через её же заголовок.
- **Расширение:** новый REST-путь, способный затронуть снятую задачу (delete,
  subtasks) — добавить тест по образцу первого; новый способ передачи «агентского»
  происхождения — новый тест на его гейт.

### McpInstructionsLengthGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Integration/McpInstructionsLengthGuardTests.cs`
- **Инвариант:** поле `instructions` ответа `initialize` у MCP-серверов ≤ 1950 символов
  (Claude CLI усекает его на ~2 КБ, а справочники серверов уезжают именно туда).
- **Источник:** тест-комментарий: «проверено живым пробом — из 6 КБ маркеров доехали
  первые ~2050 символов»; ADR/документ в репозитории не найден → источник не указан.
- **Как ловит:** поднимает живой node-процесс `mcp/personas-server/index.js` и
  `mcp/tasks-server/index.js`, шлёт JSON-RPC `initialize`, меряет длину `instructions`.
  Для personas-server — Theory по всем комбинациям флагов `PERSONAS_WRITE`/
  `PERSONAS_BINDINGS` × задан/не задан `PERSONAS_PROJECT_ID` (справочник зависит от
  них). `Skip.If` — node недоступен / файл не найден.
- **Слепые зоны:** только personas- и tasks-серверы; остальные MCP-серверы (widgets,
  watch, codegraph, workspace) не покрываются; вне node-окружения тесты skip-нуты.
- **Расширение:** новый node-MCP-сервер с `instructions` — добавить `Fact` с его
  путём и env по образцу `TasksServer_InstructionsВПределахЗапаса`.

### CoordinatorWriteGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/CoordinatorWriteGuardTests.cs`
- **Инвариант:** координатор не пишет файлы обходными shell-командами: раньше
  Write/Edit блокировались, а `Bash`'s `cat > file << EOF` проходил в обход.
- **Источник:** тест-комментарий: «Э7-фикс, находка Веры Major №1»; §Э7 «Живая приёмка
  (Вера, Playwright)» `docs/architecture/team-implement-mode.md`; InlineData «волна 3»
  — обходы, найденные адверсарным аудитом Глеба.
- **Как ловит:** unit-теории над статикой `CoordinatorWriteGuard`: `IsShellTool`
  (Bash/PowerShell в обоих регистрах — true; Edit/Write/Grep/Task — false) и
  `LooksLikeFileWrite` — позитив (15 команд записи: heredoc, `echo >`, `sed -i`,
  PowerShell `Set-Content`/`New-Item`/`Copy-Item`, `awk ... >`, `mv`, `cp`, reverse-
  quote-heredoc) и негатив (`dotnet build/test`, `git status/diff`, `ls`,
  `npm run build`, редирект лога, null/пусто).
- **Слепые зоны:** детектор — набор регулярных выражений, не AST: новый способ записи
  вне списка (напр. python/perl one-liner, `rsync`, `tar`-распаковка) не поймается;
  регистр и диалект shell за пределами Bash/PowerShell не покрываются.
- **Расширение:** новый найденный обход — новая строка `InlineData` в
  `LooksLikeFileWrite_ИзвестныеСпособыЗаписи_True`; новый легитимный паттерн (чтобы не
  ложно падал) — строка в тест-False-наборе.

### DesktopMcpToolsetStabilityTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/DesktopMcpToolsetStabilityTests.cs`
- **Инвариант:** состав инструментов desktop-server (`tools/list` — ровно 6: devices,
  screen, ui, act, open, run) не «мигает» при любом окружении и состоянии хода: набор
  входит в сигнатуру запуска CLI (`BuildLaunchSignature`), мерцание = перезапуск процесса
  claude со ВСЕМИ MCP-серверами («Stream closed», «No such tool available»).
- **Источник:** ADR-008 (desktop-agent) — «Существующий `McpToolsetStabilityTests` новый
  сервер не покрывает, а инвариант тот же и такой же смертельный»; инциденты-аналоги —
  WORKSPACE_WRITE (по интенту) и TASKS_EXECUTE (по глубине делегирования).
- **Как ловит:** три уровня: (1) живой node-процесс `mcp/desktop-server/index.js` —
  `tools/list` при 3 видах окружения (штатный env; `DESKTOP_API_TOKEN=` пустой = грань
  выключена; ни одной переменной) — состав обязан совпасть с константой `ExpectedTools`;
  (2) строковый скан исходника: тело `const TOOLS = [` не содержит `process.env`/`if (`/
  `.push(` (объявление целиком, без условий), `callDevice` — без `retry` (клик/ввод не
  идемпотентны), тексты исходов (`applied_unverified`, `no_visible_change`, `unknown`) —
  с запретом повтора, контент экрана/снапшота — в контейнере `untrusted(...)` /
  «НЕДОВЕРЕННЫЕ ДАННЫЕ»; (3) живой запуск `claude --disallowedTools <deny-имена>` — CLI
  обязан завершить ход с exit 0 без «matches no known tool» (класс дефекта MultiEdit).
  Плюс: `BuildDesktopContext` (SessionManager) не смотрит на `_currentTurn*` (скип, пока
  не реализован), `device` — человеческое имя, а не GUID, в схемах всех инструментов
  кроме `desktop_devices`; `desktop_act` — `maxItems=10`, enum действий
  click/type/key/scroll/focus, `desktop_screen` — `scope` дефолт `window` + `snapshotId`.
- **Слепые зоны:** строковый скан ищет маркеры в исходнике JS — переименование
  `const TOOLS = [`/секции-разметчики роняет сам сторож, но обход через новый
  `require`/импорт мимо этих маркеров не виден; живой node-прогон skip-нётся без node.
- **Расширение:** новый desktop-инструмент — строка в `ExpectedTools` (состав
  «полный и единственный»); новый env-зависимый сервер — свой класс по образцу
  (живой `tools/list` + скан исходника + deny-прогон CLI).

### FalAccumulatorRaceRegressionTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/FalAccumulatorRaceRegressionTests.cs`
- **Инвариант:** стоимость FAL не теряется при оживлении аккумулятора:
  `PublishFalCostAsync` читает `entry.Accumulator` и пишет стоимость атомарно под
  `_falPersistLock`, а не выбирает ветку (диск vs аккумулятор) вне лока.
- **Источник:** тест-комментарий: ревью волны 2, Blocker 1 (`PublishFalCostAsync`
  читал аккумулятор до лока, `EnsureAccumulatorAsync` под ним успевал загрузить историю
  без стоимости, и первый `SaveSnapshotAsync` затирал дописанную на диск стоимость);
  фикс волны 3. ADR в репозитории не найден (источник — сам тест).
- **Как ловит:** один детерминированный сценарий, воспроизведённый руками: тест берёт
  внутренний `_falPersistLock` через reflection, держит его (имитируя тело
  `EnsureAccumulatorAsync`), запускает `PublishFalCostAsync` (тот обязан парковаться на
  семафоре, а не выбрать ветку — `IsCompleted == false` на 200 мс), оживляет аккумулятор
  под локом (история без стоимости), отпускает; после публикации + `SaveSnapshotAsync`
  стоимость обязана остаться в истории (`LoadAsync` → ровно 1 `StoredFalCostMessage`).
- **Слепые зоны:** один зафиксированный сценарий; другие порядки вклинивания (напр.
  два одновременных publish, publish во время trim'а) не покрыты; тест зависит от
  имени поля `_falPersistLock` (reflection) — переименование роняет сам тест.
- **Расширение:** новый вид стоимости/аккумулятора в Llm (сейчас FAL/Glif) — свой
  regression-сценарий по образцу; общий паттерн «ветка выбрана вне лока» — искать в
  `*Service`'ах с `SemaphoreSlim` и писать сценарий, как здесь.

### HiggsfieldFailureClassRegressionTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/HiggsfieldFailureClassRegressionTests.cs`
- **Инвариант:** failure-classification Higgsfield: немой путь `EnsureFresh` (Connected,
  но `AdminOwnerId` пуст / сервисная запись реестра пропала) обязан писать
  `NeedsAuth` в `McpStatusStore` с текстом причины, а снимок инструментов старше
  `SnapshotMaxAge` (24 ч) не отдаётся модели — фантомные инструменты опаснее пустого
  состава.
- **Источник:** тест-комментарий: мутационное ревью Глеба, коммит `3aa69833` — код
  приняли, но без тестов откат обеих правок давал 103/103 зелёных. ADR в репозитории не
  найден (источник — сам тест).
- **Как ловит:** 4 факта: (1) `EnsureFresh` при `Connected=true` + пустом `AdminOwnerId`
  → null-токен + запись `NeedsAuth` с «AdminOwnerId» в тексте; (2) тот же, но запись
  реестра удалена → текст указывает на «сервисную» запись; (3) контраст:
  `Connected=false` (чистая инсталляция) → молчаливый null БЕЗ записи в store (иначе
  красная карточка у каждого нового пользователя); (4) `ToolsFor` с реальным
  `SessionManager` (чтобы `TryResolveSession` прошёл и дошло до проверки потолка) при
  `_lastSuccessAt` старше 24 ч → пустой состав; граничная Theory: 23:59:59 — снимок
  ещё отдаётся, 24:00:01/25 ч — нет (ровно 24 ч не проверять детерминированно:
  comparison строгий `>` и до вызова ToolsFor проходит несколько мс).
- **Слепые зоны:** рефлексия по именам полей (`_cachedTools`, `_lastSuccessAt`,
  `_lastFetchAttempt`) — переименование роняет тест; покрывает только Higgsfield
  (аналогичные тулсеты с age-кэшем — свой класс); `ScheduleRefresh`-путь сети под
  NoOp-заглушкой, не по настоящему.
- **Расширение:** новый OAuth-сервис с немым путём → тесты 1–3 по образцу; новый
  тулсет с age-кэшем → тесты 4–5 (с реальным SessionManager — иначе проверка потолка
  недостижима).

### IlBoundaryRegressionTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/IlBoundaryRegressionTests.cs`
- **Инвариант:** IL-скан, включённый в сторожей границ (волна 1, задача `8beee75e`),
  обязан находить все 7 известных швов, которые прежние рефлексионные сторожи не видели
  (3 из них живут во вложенных типах: async-state-машинах и замыканиях) — иначе
  сторож выглядит работающим, а стал декоративным.
- **Источник:** `docs/research/il-boundary-scan-2026-09.md` (§«Вердикт по слепым пятнам»
  + таблица «шов → где нашлось») и CLAUDE.md «Известное ограничение» (6 швов,
  объявленных «вслепую, для будущего IL-скана»); шов «Tasks → Hubs.TaskHubExtensions»
  снят после Этапа 5 Ф4 (extension-метод удалён).
- **Как ловит:** один факт: форс-загрузка сборок вынесенных вертикалей
  (`typeof(...).Assembly` в стат. конструкторе, т.к. .NET 5+ лодит сборку по
  первому использованию), затем `BoundaryIlScanner.CollectAllReferencedTypes` по
  исходным типам 6 живых швов (DeployHost→IGitRepoChecker, DeployAgentLockAdapter→
  Backup.InstanceLock, ReaderService→SsrfGuard, PersonaMemoryAutolearnService→
  SessionTranscript, DockerProcessRunner→TranscriptRoots, SpecialtySettingsStore→
  SpecialtyCatalog) и ассерт, что каждая игла найдена в referenced-множестве.
- **Слепые зоны:** проверяет ВИДИМОСТЬ скана, не политику: 6 (из 7) зафиксированных
  швов; новый шов вне списка не замечен; переименование типа/иглы роняет тест
  (свежая проба `Memory → SessionTranscript` — цель в Core, т.к. шов переехал в
  спину, и форма статического вызова сохранена).
- **Расширение:** новый шов (вертикаль → чужой тип) — строка в массиве `checks`;
  шов, умерший после выноса вертикали, — вычеркнуть строку, как «TaskHubExtensions».

### LocalLlmClientBoundaryTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/LocalLlmClientBoundaryTests.cs`
- **Инвариант:** тип локального движка (`ILocalLlmClient`, `OllamaClient`,
  `LlamaServerClient`) не упоминается ни одним типом вне сборки
  `ClaudeHomeServer.Llm`, кроме единственного легитимного прямого потребителя —
  `SessionManager` (голосовой ход идёт мимо `ICheapTextRunner` и держит клиента
  напрямую).
- **Источник:** `docs/architecture/llm-providers.md` (потребители держат
  `ILocalLlmClient`; «голосовой разговор — единственное неагентное чатовое место
  `chat-voice`») + тест-комментарий: сторож закрывает три обхода маршрутизации
  (`OllamaActionRankService` раньше — прямой вызов, `UsageController` — снимок
  настроек, фоновый прогрев из Program.cs — теперь IHostedService); прецедент
  «вакуумного прохода» — ревью выноса Git (17/17 зелёных при пустом наборе).
- **Как ловит:** один факт на `BoundaryIlScanner.CollectAllReferencedTypes` (видит
  static-вызовы, DI-резолвы `GetRequiredService<T>()`, generic-аргументы,
  async-state-машины, замыкания): все типы всех ClaudeHomeServer-сборок, кроме
  Llm-сборки, сканируются на упоминание 3 игл; allow-list — точные имена
  (`SessionManager` + nested-суффикс `+`). Форс-загрузка Llm-сборки
  (`typeof(LocalActionRouter).Assembly` в стат. конструкторе) + анти-вакуумный
  ассерт `asms.Count >= 4` + «тип не найден → Fail с диагнозом».
- **Слепые зоны:** IL-скан не видит string-резолвы (`Type.GetType`,
  `Assembly.LoadFrom`) и `[FromKeyedServices]`; 1-элементный allow-list — если
  появится второй легитимный прямой потребитель (например, новый voiced-путь),
  придётся расширять; переименование одной из 3 игл роняет тест (и это
  намеренно).
- **Расширение:** новый легитимный прямой потребитель → строка в `AllowedOwners`
  (с обоснованием в комментарии, как у SessionManager); новый тип локального
  движка → игла в массив `needles`.

### LucideGlyphWhitelistGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/LucideGlyphWhitelistGuardTests.cs`
- **Инвариант:** белый список значков `LucideGlyphs.All` (производная генерируемой
  копии lucide-icon-names.g.txt) совпадает с ключами loader-карты
  `dynamicIconImports.mjs` установленного `lucide-react` — сервер принимает ровно
  то множество, что фронт может нарисовать.
- **Источник:** ADR-009 (project-icon-glyph) §5, ревизия 17.08.2026: прежняя
  редакция требовала рукописную карту `GLYPHS`; дрейф копии «тихий, как и раньше»:
  сервер принимает имя, которого фронт не нарисует (или отбрасывает годное) —
  значок молча не появляется, в логах ничего.
- **Как ловит:** один факт: парсит ключи
  `frontend/node_modules/lucide-react/dist/esm/dynamicIconImports.mjs`
  (регулярка `"([a-z][a-z0-9-]*)": () =>`; звезда, а не плюс, — в наборе
  однобуквенное имя «x»), сверяет с `LucideGlyphs.All` (NotEmpty — пустой список =
  ресурс не загрузился, а не «всё совпало»), требует пустость обеих разниц;
  `Skip.If` — lucide-react не установлен (в бэковом CI node_modules нет — дрейф там
  ловит зеркальный vitest-сторож фронта, блокирующий шаг CI).
- **Слепые зоны:** только на машине с node_modules; сверяет ИМЕНА, не схемы
  (SVG-свойства, strokeWidth) — за ними ADR-009 §5.1; регенерация копий —
  `node scripts/gen-glyph-names.mjs` из frontend/, сам генератор сторожем не
  покрывается.
- **Расширение:** ничего менять не нужно: новый пакет значков — новая копия +
  генератор; если фронт переключит loader-карту, сторож роняется на
  `FindRepoFile`/regex (маркеры — в комментарии теста).

### ProjectMapHygieneGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/ProjectMapHygieneGuardTests.cs`
- **Инвариант:** корневая `CLAUDE.md` (карта проекта) не превышает 520 строк
  (`MapLineBudget`) и не содержит мёртвых ссылок/не раскрывшихся @-импортов — карта
  едет в контекст каждой сессии целиком, каждым ходом каждого чата.
- **Источник:** `docs/research/claude-md-cleanup-2026-09.md` (инвентаризация и уборка
  2026-09-22: 1013 → 477 строк; правило «держи карту компактной» висело в шапке
  без обратной связи — «правило без гейта — пожелание») + `docs/features/
  project-map-hygiene.md` (фича «уборка карты»); порог 520 взят с запасом ~8% от
  482 строк после уборки.
- **Как ловит:** два факта на одном скане (`ProjectMapScanner`, та же фича, что за
  кнопкой «Убрать карту» — расхождение «кнопка/CI» невозможно по конструкции):
  (1) `Lines <= MapLineBudget`, иначе — отчёт с оценкой токенов и 3 самые длинные
  секции-кандидаты на вынос (с подсказкой, если секция пересказывает docs/*);
  (2) `DeadLinkCount == 0 && DeadImportCount == 0` (корневая + вложенные карты),
  с описанием каждой находки и, при одном кандидате, «чем чинить». Константа, а не
  конфиг, — осознанно: повышение порога обязано быть видимым в дифе.
- **Слепые зоны:** только размер и ссылки — длина/качество отдельных секций
  намеренно не проверяется («у теста слишком грубый инструмент»); «токены» — оценка
  (байты ÷ 3), не токен; сканер один на весь класс (IClassFixture).
- **Расширение:** новый вид «гигиены» (напр. дублирующиеся секции) — новый отчётный
  поле в `MapHygieneReport` + новый факт; перенос карты/смена формата — новый
  `FindRepoRoot`-маркер.

### SecFetchSiteGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/SecFetchSiteGuardTests.cs`
- **Инвариант:** кука-аутентификация прокси-сайта принимается только при
  `Sec-Fetch-Site: same-origin`: `SameSite=Strict` считает САЙТ, поэтому для куки
  поддомен внешнего доступа «свой» и ушёл бы с запросов проксируемого дев-сайта;
  заголовок ставит браузер, из скрипта не подделывается; отсутствие заголовка
  (не-браузер, старый браузер) — НЕ отказ.
- **Источник:** тест-комментарий (SameSite=Strict, соседний поддомен = «свой»
  запрос); ADR/документ в репозитории не найден → источник не указан.
- **Как ловит:** пять тестов над `SecFetchSiteGuard.CookieAuthAllowed` (чистый
  `HttpRequest`, без хоста): `same-origin` → true; `same-site` (основной
  защищаемый случай — чужой код на соседнем поддомене) → false; `cross-site`/
  `none` → false; отсутствующий/пустой заголовок → true; регистр значения
  (`Same-Origin`) → true.
- **Слепые зоны:** только гейт Sec-Fetch-Site: не покрывает сам транспорт
  cookie-заголовка и другие атаки (Origin/Referer не проверяются этим
  гейтом); «отсутствие = пропуск» — осознанная политика, не проверка.
- **Расширение:** новый допустимый/запрещённый `Sec-Fetch-Site`-разновидности —
  InlineData в Theory; второй домен-контекст (не iframe) — новый `Fact`.

### SsrfGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/SsrfGuardTests.cs`
- **Инвариант:** `SsrfGuard.IsPublic`/`IsPubliclyRoutableAsync`/`CheckAsync`
  отсекают loopback, приватные (10/8, 172.16/12, 192.168/16), link-local/
  cloud-metadata (169.254), CGNAT (100.64/10), 0/8, multicast, reserved,
  broadcast, benchmark, IPv6-аналоги, IPv4-mapped и NAT64-завёрнутые
  адреса; публичные — пропускают (включая границы диапазонов).
- **Источник:** ADR-005 (link-reader-server): «`SsrfGuard` (сервер) — после
  резолва DNS», «Дыры существующего `SsrfGuard`, которые надо закрыть заодно»
  (multicast и др.) + задача «SSRF-обход» 2026-08-03 (контейнер
  `claude-server`).
- **Как ловит:** теории с InlineData по каждому классу адресов, включая
  границы (172.31.255.254 vs 172.32.0.1, 192.168.255.255 vs 192.169.0.1,
  198.19/20, 223.255.255.255 vs 224.0.0.1) и NAT64-представления
  (64:ff9b::7f00:1 = 127.0.0.1); `CheckAsync` → `AddressCheck.Private/
  DnsFailed/Public` по URI.
- **Слепые зоны:** только классификация адресов: редирект-политика
  Reader'а (ADR-005 «иначе он обойдёт SSRF-проверку») — другой тест;
  DNS-rebinding между резолвом и connect не покрывается.
- **Расширение:** новый «приватный» диапазон (напр. новый CGNAT-блок) —
  InlineData в соответствующую Theory; новый канал резолва — свой `CheckAsync`
  -кейс.

### SubscriptionWindowMismatchGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/SubscriptionWindowMismatchGuardTests.cs`
- **Инвариант:** «чужой» setup-токен: расхождение времени сброса 5h-окна между
  setup-токеном (probe/turn) и профильным логином (oauth) одного ключа ≥ порога
  → алерт ровно на смену состояния (не каждый тик, из ротации — никогда);
  на несвежих снимках, мелком расхождении и без профильного логина — тишина;
  текст алерта несёт DisplayName (фолбэк «Аккаунт Claude»), а не сырой ключ
  (инвариант e862a991).
- **Источник:** тест-комментарий: инцидент 20–23.08.2026 (oauth-канал видел
  сброс на 59 минут позже setup-токена — ключ «не свой»); ADR в репозитории не
  найден → источник не указан.
- **Как ловит:** 10 сценариев против `UsageService.Record` (реальные записи
  usage, source=oauth/probe/turn) + `guard.CheckAsync`: 59-мин расхождение → 1
  алерт на 2 тика; 2-мин расхождение / только probe / снимки старше 1 h →
  тишина; схождение окон → гасит флаг + `[SubscriptionGuard] ... согласованы`
  в stderr, повторное расхождение → 2-й алерт; провал одного канала (oauth
  401) → флаг не гасит, повторного алерта нет; turn-снимок живого хода
  образует пару как probe. `Console.SetError` процесс-глобален →
  `[Collection(ProcessGlobalState)]`.
- **Слепые зоны:** только каналы usage (oauth/probe/turn) одного ключа;
  «свежесть» — 1 h (константа гварда, граница не проверяется); троттлинг
  UsageService обходит разными статусами каналов (прим. в тесте), сам
  троттлинг не проверяет.
- **Расширение:** новый канал снимков 5h-окна (новый source) — сценарий с
  `Record(..., source:)`; новый текст/политика алерта — ассерт на
  `CountingNotifier.LastTitle/LastBody`.

### InotifyLeakRegressionTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/InotifyLeakRegressionTests.cs`
- **Инвариант:** сбой inotify-слежки не плодит живые экземпляры наблюдателей:
  пересоздание из колбэка Error при исчерпанном бюджете (рекурсия) — исходная болезнь
  инцидента; после перевода на `RecursiveDirectoryWatcher` отказ слежки на отдельном
  каталоге — частичный (наблюдатель живёт), пересоздание — только на смерть наблюдения
  целиком.
- **Источник:** `docs/architecture/file-watching.md` §«Два вида сбоя, и путать их
  нельзя»: инцидент 2026-09-19, Linux-прод, 13 → 8 076 брошенных экземпляров inotify
  (пересоздание при исчерпанном бюджете); перевод на RecursiveDirectoryWatcher 2026-09-22.
- **Как ловит:** три сценария (только Linux, `[Collection(Inotify)]`): (1) дерево с
  цепочкой глубже PATH_MAX (ENAMETOOLONG — тот же путь кода, что ENOSPC на проде, но
  под любым пользователем: chmod 000 root обходит, EMFILE роняет старт исключением, а не
  Event) — всплеск 500 папок+файлов, `RecreateDelayMs=50`: пик fd ≤ baseline+2,
  после снятия наблюдателя fd ≤ baseline, наблюдение за остальным деревом живое,
  `RecreateCount == 0`; (2) `WatchPath` на тот же путь без наблюдателя поднимает
  наблюдение на ТОЙ ЖЕ записи (`.Should().BeSameAs` — подмена теряла бы PendingPaths);
  (3) `TurnFileWatcher.Start` упал на fd-лимите (setrlimit, EMFILE): `IOException`,
  ни прежний, ни недоделанный FileSystemWatcher не остаются, `TurnErrorClassifier` —
  `None` (лимит ОС не жжёт цепочку фолбэка), после снятия лимита старт штатен.
- **Слепые зоны:** только Linux (skip на Windows); ENAMETOOLONG/EMFILE имитируют
  ENOSPC, но не имитируют его; реальное исчерпание бюджета слежек пользователя (общий
  на ОС-пользователя) в тесте «не исчерпать безопасно»; взаимоблокировка старого кода
  закрыта таймаутом сценария, а не по конструкции.
- **Расширение:** новый потребитель `RecursiveDirectoryWatcher` (TurnFileWatcher уже
  покрыт) — свой лимитный сценарий; новый вид сбоя слежки — классификатор + тест
  против `WaitInotifyAtMost`.

### IrreversibleCommandGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/IrreversibleCommandGuardTests.cs`
- **Инвариант:** стоп-список необратимых команд режима «Авто» (`IrreversibleCommandGuard`)
  держит обе стороны: каждая форма разрушительной команды (rm -rf, git push --force,
  pipe-to-shell, mkfs/dd, shutdown, …) ловится; повседневные (build/test, git
  status/diff/log, push без force, --force-with-lease, -d, -n dry-run) — не гасятся.
- **Источник:** `docs/architecture/features.md` §«Режимы прав»: «Авто» не спрашивает на
  shell-команды, «кроме стоп-списка необратимых (`IrreversibleCommandGuard`:
  рекурсивные удаления, разрушительный git, pipe-to-shell, диски, выключение)».
- **Как ловит:** четыре Theory: `IsShellTool` (Bash/PowerShell оба регистра — true;
  Edit/Write/Grep/Task — false); `LooksIrreversible` — 27 позитивных форм (порядок/
  склейка флагов `rm -rf/-fr/-r -f`, `rmdir /s /q`, `Remove-Item -Recurse -Force`,
  `git push --force/-f/--delete`, `git reset --hard`, `git clean -fd/-fdx`,
  `git branch -D`, pipe-to-shell `curl|sh/wget|bash/sudo bash`, mkfs/dd/diskpart/
  format, shutdown/reboot) и 25 негативных (включая близкие к списку, но безопасные:
  `rm -r` без force, `--force-with-lease`, `branch -d`, `clean -n`, `format-patch`,
  `curl -o` без пайпа).
- **Слепые зоны:** детектор — набор регулярных выражений, не AST: новый способ
  (zsh/fish one-liner, `python -c "os.remove"`, `rsync --delete`, распаковка tar) не
  поймается; shell-диалекты за пределами Bash/PowerShell не покрываются.
- **Расширение:** новый найденный обход/форм — строки `InlineData` в соответствующую
  Theory; новый легитимный командный паттерн (чтобы не ложно падал) — строка в
  False-набор.

### TeamAsyncAgentStallGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/TeamAsyncAgentStallGuardTests.cs`
- **Инвариант:** гард молчаливого тупика подавляется async-агентом только
  ОГРАНИЧЕННО: async-агент жив + метки нет (первый вызов) → подавляем (разумно
  ждать `team:work`); длительность подавления ≤ 10 мин (`DefaultSuppressionTimeout`,
  строгая `<`) → гард молчит; дольше → карточка молчаливого тупика; без
  async-агента — штатная логика.
- **Источник:** тест-комментарий: задача `b63fd8ea` — раньше подавление висело
  бессрочно (карточка молчаливого тупика, которую async-агент должен был
  снять, не поднималась никогда). ADR в репозитории не найден → источник не
  указан.
- **Как ловит:** шесть unit-фактов над чистой функцией
  `TeamAsyncAgentStallGuard.ShouldSuppress` (без процесса/CLI/`DateTime.UtcNow`
  — «по образцу `ClaudeSessionWatchdogMappingTests`»): нет агента (null/
  устаревший stallSince) → false; агент + метки нет → true; 5 мин < порога →
  true; 11 мин > порога → false; ровно на границе → false (строгое `<`, иначе
  карточка опоздала бы на тик); константа `DefaultSuppressionTimeout == 10 мин`
  (контрактный размер потолка — меняется осознанно).
- **Слепые зоны:** только чистая функция: реальная метка `team:work`, живые
  тики гарда и интеграция с SessionEntry не покрываются; 10 минут зафиксированы
  как поведенческая граница, но источник выбора значения — только комментарий.
- **Расширение:** смена политики (напр. отдельный порог для разных стадий
  тупика) — новые InlineData-факты по образцу; новый канал «async-агент
  жив/умер» — новый аргумент функции + пара фактов.

### TestingDefaultProjectsPathGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/TestingDefaultProjectsPathGuardTests.cs`
- **Инвариант:** тестовый хост (`appsettings.Testing.json`, без override
  `DefaultProjectsPath` из `TestWebApplicationFactory`) стартует с ПУСТОЙ
  `DefaultProjectsPath` — иначе `UserHomeResolver` уводит нового пользователя в
  прод-`/projects` (контейнерный путь, на Windows маппится на `C:\ClaudeHome`),
  а не в temp-дерево.
- **Источник:** тест-комментарий (маппинг `C:\ClaudeHome`, «уведёт в прод»);
  ближайший документ — `docs/architecture/conventions.md` (локальные пути
  `DefaultProjectsPath`); ADR не найден → источник не указан.
- **Как ловит:** один факт: `WebApplicationFactory<Program>` с
  `UseEnvironment("Testing")` (своя `NoOverrideFactory` — без override'а
  фабрики) → `AppSettingsService.Get().DefaultProjectsPath` пуст/null.
- **Слепые зоны:** только конфигурационный ключ: поведение
  `UserHomeResolver` при пустом пути (null для любого пользователя)
  не проверяется; другие секции `appsettings.Testing.json` — вне скоупа.
- **Расширение:** новый «тестовый» конфиг-ключ, который должен отличаться от
  прода (чтобы тесты не писали в прод-дерево) — свой факт по образцу; новый
  environment-профиль — новая `*Factory` + тест.

### MetricTagGuardTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Telemetry/MetricTagGuardTests.cs`
- **Инвариант:** ЗНАЧЕНИЯ тегов метрик ограничены по кардинальности:
  `ServerMetrics.AllowedTags` стережет только ИМЕНА тегов, а значения
  (`tool_name` — путь запроса с GUID, `model` — свободная строка) без
  ограничителя дарили ClickHouse вечные ряды до конца retention; `MetricTagGuard`
  + `TagValueLimiter` (потолок на число РАЗНЫХ значений) схлопывают мусор в
  `Overflow`/`Unnamed`.
- **Источник:** тест-комментарий (ClickHouse, retention; примеры: MCP-сервер
  без `X-Mcp-Tool` → путь `/api/projects/{guid}/files/read` уезжал в
  `tool_name`, `model` — свободный ввод из `PUT /api/projects/{id}/sessions/{sid}`);
  ADR/документ в репозитории не найден → источник не указан.
- **Как ловит:** три уровня: (1) чистая функция `MetricTagGuard.Tool/Model` —
  null/пусто → `Unnamed`, реальные имена/модели (включая `qwen2.5:7b`,
  `direct:openai/...`) проходят, пути/мусор/свободный text/длинные > 64 →
  `Overflow`; (2) `TagValueLimiter`: потолок 3 разных значений, 4-е → `Overflow`,
  известные значения продолжают проходить (счётчики не рвутся), форм-сбой
  бюджета не ест; (3) интеграция через реальный `MeterListener` над
  `ServerMetrics.RecordMcpCall`/`RecordLlmDuration`: путь запроса НЕ
  доезжает до тега `tool_name` (главная регрессия: «проверяем измерение, а
  не чистую функцию — правило легко обойти, передав значение мимо
  ограничителя»).
- **Слепые зоны:** 2 тег-ключа (tool_name, model): другие измерения/теги
  (напр. provider-значения, session-идентификаторы) вне скоупа; лимит
  «3/2» — тестовые константы, реальные потолки в коде `MetricTagGuard` не
  сверяются.
- **Расширение:** новый тег с внешними значениями → `MetricTagGuard.<Ключ>` +
  InlineData + интеграционный `Capture`-факт; смена реального потолка
  кардинальности → сверка с `MetricTagGuard`/конфиг (сейчас потолок живёт в
  коде гварда, не в этом тесте).

### RootSubsystemBoundaryTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/RootSubsystemBoundaryTests.cs`
- **Инвариант:** top-level синглтоны корня `ClaudeHomeServer.Services`
  (Namespace ровно `ClaudeHomeServer.Services`, без поддеревьев `Services.X`)
  не ссылаются на ТИПЫ подсистемных вертикалей: допустимы только «спинка»
  (System/`Microsoft`, `Models`, `Services.Http/Composition/Mcp`, `Protocol`,
  сборка Core), инфраструктурные под-вертикали из `RootAllowedSubVerticalPrefixes`
  (Llm, Execution, Memory, Knowledge, Images, CodeGraph, Dossiers, Spend,
  TriggerSources, Modules, Docs, Desktop) и точечные типы
  `RootAllowedExactTypes`; peer-root (root-типы между собой) разрешены;
  backbone-типы (`ExcludedRootTypes`: PersonaManager, ProjectManager,
  SessionManager, UserStore, ChatHistoryService, FileService,
  Notification*) — шовный код, исключён и как источник, и как цель.
- **Источник:** тест-комментарий: «находка ревью 50f3b849 — добавление
  `PersonaManager` в конструктор `KnowledgeService` давало зелёный
  boundary-тест, потому что KnowledgeService жил в корне Services и его не
  видел ни один страж»; «спинка»/allow-list — `SharedAllowedPrefixes`
  ADR-014 (internal-subsystems).
- **Как ловит:** один факт `RootServices_НеСсылаетсяНаПодсистемныеВертикали`:
  рефлексия + IL-скан (`BoundaryIlScanner`) по полям, конструкторам,
  свойствам, сигнатурам и ТЕЛАМ методов всех root-типов; каждая referenced-
  игла сверяется со спикой/allow-list/peer/Excluded; форс-загрузка ~25
  вынесенных сборок в стат. конструкторе (иначе AppDomain их не видит).
  `TaskManager` вынесен из Excluded (волна 4C, шаг 1 — уехал в вертикаль
  `Services.Tasks`, его держит свой сторож).
- **Слепые зоны:** сканируются только top-level типы (Namespace строго
  `ClaudeHomeServer.Services`): nested-типы в корне и под-namespace
  `Services.*` (их держит SubsystemBoundaryTests) — вне этого сторожа;
  `RootAllowedExactTypes`/`ExcludedRootTypes` — ручной список (подростировка
  — мутацией).
- **Расширение:** новая «спинка»-под-вертикаль (3+ root-зависимостей) —
  префикс в `RootAllowedSubVerticalPrefixes`; единичный тип — запись в
  `RootAllowedExactTypes`; новый backbone-синглтон (держит ссылки на
  вертикали) — запись в `ExcludedRootTypes` (с осторожностью: «держать их тут
  на всякий случай нельзя» — см. комментарий).

### SubsystemBoundaryCoverageTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/SubsystemBoundaryCoverageTests.cs`
- **Инвариант:** полнота таблицы `SubsystemBoundaryTests.Boundaries`: (1)
  каждая реализация `IAppSubsystem` в сборках имеет строку в таблице; (2)
  ЛЮБОЙ namespace `ClaudeHomeServer.Services.*` с «настоящим» типом (top-level,
  не compiler-generated) покрыт строкой Boundaries (префиксно) или явным
  «спинка»-исключением (`Services.Http/Composition/Mcp/DynamicModules/Files`).
- **Источник:** ADR-014 (internal-subsystems) + прецеденты из комментариев:
  `Services.Watchdog` (5 файлов, ни одного IAppSubsystem — до проверки
  отсутствовала в Boundaries); волна 4, шаг 0: 5 неймспейсов (Services.Llm,
  Docs, Turn, Prompts, Execution; ~35 089 строк) жили без строк и без
  сторожа; мутация ревью `23d353d7` (17/17 зелёных при нуле типов); мутация
  этапа 4 (MAJOR 1, сентябрь 2026): 36/36 зелёных при заведомом ребре
  `Services.Team → Services.Video`, default-deny не срабатывал.
- **Как ловит:** два факта с форс-загрузкой ~20 вынесенных сборок (стат.
  конструктор: Video/Yandex/Reader/CodeGraph/Skills/Git/Notes/Tts/Personas/
  Diagnostics/WebSearch/Changelog/Docs/Knowledge/Modules/Spend/Tasks/Turn/
  Dossiers/Memory/Desktop/Backgrounds/ProjectIcons/Terminal/Llm/Images/
  Prompts) и анти-вакуумными ассертами (`assemblies >= 2`, типы
  `NotBeEmpty` — ориентир ~4230, реализации IAppSubsystem — ориентир 14,
  namespaces — ~25): (1) namespace-множество реализаций IAppSubsystem ⊆
  bound-корней; (2) namespacesWithTypes ⊆ covered(Boundaries, prefix-match)
  ∪ excluded; отдельный ассерт «дом Files-примитива — только
  ClaudeHomeServer.Core» (исключение `Services.Files` действует на ВСЕ
  сборки, поэтому утверждается, где его типы живут).
- **Слепые зоны:** «спинка»-исключения — ручной список (5 namespace;
  вертикаль, ошибочно попавшая в exclusion, не видна, пока не появится
  тип); nested- (`Foo+Bar`) и compiler-generated (`<...>`) типы
  игнорируются — namespace, где есть только они, «не существует»; новый
  namespace без top-level-типов (только partial/interface без реализации)
  проходит.
- **Расширение:** новая вертикаль/подсистема — строка в `Boundaries`
  (SubsystemBoundaryTests); namespace, оказавшийся «спиной» — запись в
  `excludedNamespaces` (с обоснованием — «подумай, правда ли это спинка»);
  `verticalOnlyNamespaces` (вертикали без IAppSubsystem) — сознательно
  пуст: Watchdog/Terminal/Team получили строки в Boundaries.

### McpToolsetStabilityTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/McpToolsetStabilityTests.cs`
- **Инвариант:** состав `tools/list` MCP-серверов (ключи серверов + отпечаток
  `shapes` в `BuildTurnMcpConfig`) постоянен в рамках сессии и НЕ зависит от
  свойств ХОДА (глубина делегирования, текст, флаги подавления): состав входит
  в сигнатуру запуска CLI, и любое мерцание перезапускает процесс claude со
  ВСЕМИ MCP-серверами («Stream closed», «No such tool available»).
- **Источник:** `docs/architecture/mcp-servers.md` §«Инвариант: состав
  инструментов не зависит от хода» («Наступали трижды»: `WORKSPACE_WRITE` по
  интенту хода, `PERSONAS_WRITE`/`MENTIONS` по тексту, `TASKS_EXECUTE`/срезание
  секций `chats`/`destructive` по `agentDepth` — инциденты без дат) + CLAUDE.md
  §«MCP-серверы продукта»; ADR-008/ADR-012/ADR-004/ADR-011/ADR-003 — только
  ссылки.
- **Как ловит:** два независимых механизма: (1) **строковый скан исходников**
  (IndexOf/Regex по телам методов, `://`-комментарии выкидываются, НЕ
  reflection): `СоставИнструментовХода_НеЗависитОтСостоянияХода` (тело
  `BuildTurnMcpConfig` в `ClaudeSession.cs` не содержит
  `_currentTurnAgentDepth`/`turnText`/`_currentTurnSuppressTasksExecute`),
  `РубильникиСерверов_ГейтятсяТолькоПоПерсоне` (theory по widgets/codegraph/
  personas/consultants/`mcp:`), `СоставToolsFor_Тулсетов_НеЧитаетСостояниеХода`
  (скан `Services/Mcp/Http/*.cs`), `ShapeПерсон_ЧитаетЕдинуюФормулуMentions`;
  (2) **запуск живых MCP-серверов** (node, JSON-RPC `tools/list`,
  env-вариации): `PersonasSetDefault_ВСоставеВсегда_ДажеБезМодулей`,
  `MemoryDossierСекция_*`, `ContextList_ВСоставеПриЛюбомНабореСекций`,
  `ContextListГейт_РешаетсяФлагомВладельцаАНеХодом`.
- **Слепые зоны:** скан по ИМЕНАМ переменных: ход, переданный в метод под
  иным именем (не `_currentTurn*`/`turnText`/`GetActiveTurnDelegation`), не
  виден; `Skip.If(path is null)` — вне дерева репозитория (и без node)
  сторож молча скипается; переименование `MapMcpPath`/сигнатур роняет сами
  тесты (зависимость `Файл:строка` — отмечена в шапке); живой tools/list
  замеряет только personas/memory/workspace-серверы, состав остальных
  (notes, tasks, desktop, реестра `mcp:*`, http-тулсетов) — только
  строковый скан; проверяется НАЛИЧИЕ/отсутствие строк, не поведение: два
  разных гейта, дающих один результат, пропустит.
- **Расширение:** новый MCP-сервер — Inline-случай в
  `РубильникиСерверов_ГейтятсяТолькоПоПерсоне` (сигнатура `Build*-Context` +
  tool-ключ) и, если есть env-зависимый состав, тесты живого tools/list
  (паттерн `ListMemoryTools`); новая секция/модуль — Theory
  `СекцииПоРоли_ГейтятсяТолькоПоПерсоне` / тест
  `МодульЗаметок_ГейтитсяТолькоПоПерсоне`; новый http-тулсет —
  автоматически подпадает под `СоставToolsFor_...` (скан каталога).
  ADR-008 прямо предупреждает: «существующий McpToolsetStabilityTests новый
  сервер не сторожит» — расширение руками.

### SubsystemBoundaryTests

- **Файл:** `backend/ClaudeHomeServer.Tests/Services/SubsystemBoundaryTests.cs`
- **Инвариант:** тип из vertical-namespace (`ClaudeHomeServer.Services.<X>`)
  не ссылается ни на один тип ДРУГОЙ vertical — только на «спину»
  (System/`Microsoft`, `Models`, `Core`, `Services.Http/Composition/Mcp`,
  `Protocol`) и объявленные allow-list-швы; нарушение = тест красный
  (default-deny).
- **Источник:** ADR-014 «Внутренние подсистемы» (2026-09-01, «Принято»):
  этап 0 — контракт `IAppSubsystem` + пилот Video, этап 1 — этот сторож;
  правило — §«Правило зависимостей вертикалей», механика — §«Устройство
  сторожей границ»; CLAUDE.md §«Внутренние подсистемы (Services/
  Composition)». Конкретный инцидент (без даты): прежнее единое
  allow-list-поле с записью `ClaudeHomeServer.Services` открывало вертикалям
  весь `Services.*` — default-deny было «сломано»; отдельно:
  `McpToolsetStabilityTests` сломался от переименования метода (ADR-014
  :204-206, :876-897); пилот csproj (ADR-014 :232-246, :360-369) — три
  сторожа молча сканировали только Video после выноса сборок.
- **Как ловит:** default-deny + allow-list, «сборка-правда» (не текст):
  `AppDomain.GetAssemblies()` (фильтр `ClaudeHomeServer*`, минус `*.Tests`),
  сбор типов по namespace-дереву, проверка каждого referenced-типа через
  `IsAllowed` (exact FullName → префикс namespace → сборка Core) + IL-скан
  тел методов/async-машин (`BoundaryIlScanner.CollectAllReferencedTypes`).
  Ключевые тесты: `Vertical_НеСсылаетсяНаДругиеВертикали` (Theory по
  `Boundaries`), `CoreDll_НеСодержитСсылокНаПроектныеАссембли`,
  `CoreDll_СодержитТолькоРазрешённыеНеймспейсы`; таблица `Boundaries` — 36
  записей (`AllowedNamespacePrefixes`/`AllowedExactNamespaces`); форс-
  загрузка 20+ вынесенных сборок в стат. конструкторе + анти-вакуумные
  ассерты (`HaveCountGreaterThanOrEqualTo(20)`, типы `NotBeEmpty`);
  полнота таблицы держит `SubsystemBoundaryCoverageTests`.
- **Слепые зоны:** не проверяются интерфейсы/базовые классы за пределами
  четырёх мест; вне скоупа контроллеры (namespace `Controllers`) и Main-
  типы в корневом `Services` без строки; главное: `Reflection.Emit`,
  `Type.GetType(string)`/`Assembly.LoadFrom`/string-резолвы,
  `[FromKeyedServices]` (ADR-014 :956-972); дубль строки в `Boundaries` не
  ловит (ADR-014 :876-897); новая вертикальная сборка без строки форс-
  загрузки проходит вакуумно (ловят лишь анти-вакуумные ассерты);
  `IsCoreAssembly` (exact/prefix) делает любой тип Core «невидимым» обоим
  Core-сторожам — лечит только Core-тест.
- **Расширение:** (а) новая vertical — одна строка в `Boundaries` (по
  умолчанию shared-спинка + свой namespace; точечные допуски —
  `AllowedExactNamespaces`, префиксы-швы — `AllowedNamespacePrefixes`);
  (б) строка форс-загрузки `_ = typeof(X).Assembly;` в стат. конструкторах
  ВСЕХ ТРЁХ сторожей (Boundary/Coverage/Root) + поднять порог
  `HaveCountGreaterThanOrEqualTo(20)`; (в) новый шов — сначала в Core
  (узок, 1-3 метода), a vertical-зависимость за ним — допуск в таблицу.

## Правила для новых сторожей

Правила — по выжимке всех 25 разделов; каждое правило стоит на одном или
нескольких живых прецедентах из этой карты.

1. **Пиши сторожа в той же волне, что и фикс** — пока инцидент свеж в
   комментариях. Практически все сторожи этой карты — «регрессия-в-волне»:
   Dify (волна 4.1), Tasks (волна 4 team-blocker-honest), Fal (ревью волны 2,
   Blocker 1), Higgsfield (мутирование-ревью Глеба), Inotify (инцидент 19.09),
   Sub (инцидент 20–23.08), Team (баг b63fd8ea), Dify/Coordinator (Вера,
   §Э7). Сторож, дописанный после, — «на всякий случай», и источник в нём
   уже не цитируется.
2. **Держи обе стороны инварианта.** Позитив — нарушение ловится (каждая
   форма rm -rf, каждый dot-segment, каждый мёртвый путь); негатив —
   легальное поведение не гасится (`dotnet build`/`git status`/
   `--force-with-lease` проходят, тишина при 2-мин расхождении,
   `Connected=false` — без записи). Примеры: `CoordinatorWriteGuardTests`,
   `IrreversibleCommandGuardTests`, `SubscriptionWindowMismatchGuardTests`,
   `MetricTagGuardTests` (реальные имена/модели проходят),
   `HiggsfieldFailureClassRegressionTests` (контраст «не подключено» —
   тишина).
3. **Защищай от вакуумного прохода.** Тест, который «зелёный, потому что
   ничего не нашёл», — самый тихий. Паттерны, используемые картой:
   форс-загрузка вынесенных сборок (`typeof(X).Assembly` в стат.
   конструкторе) + «типа не нашёл → `Fail` с диагнозом» (IlBoundary,
   LocalLlmClient, SubsysBoundary, SubsysCoverage, Root) + нижний порог
   количества (`assemblies >= 20`, `allTypes NotBeEmpty` с ориентиром
   ~4230, `subsystems NotBeEmpty` с ориентиром 14) + «файл не найден →
   падай, а не скип» (ProjectMap). Прецеденты: ревью выноса Git (17/17
   зелёных при пустом наборе), мутация 23d353d7.
4. **Одна «правда» на источник.** Allow-list/исключения живут в ОДНОМ месте,
   а дубли сознательно убираются: `verticalOnlyNamespaces` — пуст
   (Watchdog/Terminal/Team получили строки в `Boundaries`, дубль маскировал
   удаление); `Services.Files`-исключение утверждено «только в Core»;
   `SharedAllowedPrefixes` — единый источник для всех трёх boundary-
   сторожей. Новый список, копирующий чужой — новый кандидат на расхождение.
5. **Зависимости от окружения — skip с диагнозом, не тишина.** Node
   (McpToolset, Desktop, McpInstructions, LucideGlyph), Linux
   (InotifyLeak), claude CLI (Desktop deny-прогон), node_modules (Lucide) —
   везде `Skip.If` с причиной; но ProjectMap и boundary-сторожи НАПРЯМУЮ
   падают, если дерево/сборки не найдены: «молчаливо зелёный сторож хуже
   отсутствующего».
6. **Расширение = запись в стороже, а не в коде.** Каждый раздел этой карты
   отвечает «как расширить при новой сущности»: новый tool — InlineData;
   новая vertical — строка в `Boundaries` + форс-загрузка + (если сборка
   вынесена) запись в Coverage; новый шов — игла в `checks` (IlBoundary);
   новый MCP-сервер — Inline в Рубильники + живой tools/list. Сторож,
   который при новой сущности требует новой ВЕТКИ кода, — плохой сторож:
   ADR-008 прямо предупреждает, что McpToolsetStabilityTests «новый сервер
   не сторожит».
7. **Мутация — способ приёмки.** «Сломал на секунду проверяемое поведение —
   тест обязан покраснеть, потом верни» — стандарт проекта (в карте это
   прямо проговаривается: Higgsfield «Глеб откатил правку и получил 103/103
   зелёных»; SubsysCoverage — 36/36 при заведомом ребре `Services.Team →
   Services.Video` (этап 4, MAJOR 1) и 17/17 при нуле типов (ревью
   23d353d7, прецедент «пустого прохода», который цитируют и
   SubsysBoundary, и LocalLlmClient)). Новый тест без мутационной
   проверки — не принят.
8. **Имя теста = сценарий.** `<Вход>_<ОжидаемыйИсход>`
   (`РасхождениеСброса59Минут_АлертРовноОдин`, `TraversalВDocumentId_
   ВалидныйОтказ_ЗапросНеУходит`) — имя само является спецификацией
   мутации; тесты-«проверяю, что X true» — слабее.
9. **Источник — ищи в ADR/доках, а не в памяти.** «Откуда взялся»
   (инцидент/ADR/раздел) — поле каждого раздела; если в репозитории
   источника нет (McpInstructions, Dify, Fal, Higgsfield, SecFetch, Sub,
   Team, Testing) — честно «источник не указан», а не «по легенде». Дата
   инцидента обязательна, если есть; «наступали трижды» без дат — уже
   сигнал, что ADR не дописан.
10. **Сторож не должен расти.** Пороги — константы в дифе, а не конфиг
   (ProjectMap: «поднять `MapLineBudget` — последнее средство, а не
   первое… в конфиге такая правка тихая, а тихое послабление убивает любой
   гейт»); «контрактный размер потолка» фиксируется отдельным фактом
   (Team: `DefaultSuppressionTimeout_Равно10Минутам`) — «меняться должен
   осознанно».
