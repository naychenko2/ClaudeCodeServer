# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **Держи этот файл компактным.** CLAUDE.md загружается в контекст КАЖДОЙ сессии — это
> карта проекта, а не энциклопедия. Детальные описания подсистем живут в `docs/`;
> здесь — команды, архитектура, инварианты и короткие выжимки со ссылками. Новое крупное
> описание — сразу отдельным файлом в нужном разделе `docs/` плюс выжимка со ссылкой
> отсюда (формат — как у существующих разделов). Не раздувать этот файл обратно.
>
> Раздел выбирается по роли документа: `architecture/` — как устроено, `operations/` —
> как запускать, `adr/` — почему решили так, `research/` — исследования и срезы во
> времени, `design/` — конвенция и прототипы. Карта корпуса — [docs/README.md](docs/README.md).

## Команды

> **Стандарт: сборка и тестирование — в dev-контейнере.** По умолчанию собираем и
> прогоняем приложение в контейнере (песочница для Claude + единое воспроизводимое
> окружение), а не на хосте. Подробности — [docs/operations/docker.md](docs/operations/docker.md).

```powershell
# Контейнер (из корня проекта) — основной путь
copy .env.example .env                       # один раз: пути, CLAUDE_EGRESS_PROXY
docker compose -f docker-compose.claude.yml up -d --build   # сборка + запуск, http://localhost:5000
docker exec -it claude-server claude login   # один раз: вход по подписке
docker logs -f claude-server                  # логи
docker compose -f docker-compose.claude.yml up -d --build claude-server  # пересборка после правок
```

```powershell
# Хостовый запуск (справочно, для быстрых локальных итераций)
cd backend; dotnet build
cd backend; dotnet run --project ClaudeHomeServer   # порт 5000
cd frontend; npm run dev       # порт 5173
cd frontend; npm run build     # production-сборка (tsc -b + vite)
# Vite проксирует /api и /hubs (WebSocket) на :5000
```

## Среда исполнения пользователей (local / container)

Изоляция per-**пользователь**: `User.ExecutionEnvironment` = `local` (процессы на машине
сервера с полным доступом) | `container` (общая docker-песочница `cc-sandbox`); бэкенд
всегда НА ХОСТЕ (Windows). Все 6 точек запуска процессов идут через `IProcessLauncher` /
`ILauncherFactory.ForOwner(ownerId)` (драйверы `LocalProcessRunner` / `DockerProcessRunner`);
системные one-shot (changelog, каталог моделей) — всегда local. Пути: бэкенд работает
ТОЛЬКО с хостовыми, `IPathMapper` переводит в контейнерные в момент запуска (путь вне
монтирований → ошибка, аналог SafeJoin). Домашние папки юзеров — единая точка
[UserHomeResolver.cs](backend/ClaudeHomeServer/Services/UserHomeResolver.cs)
(+ override `Projects:UserHomeOverrides` в appsettings.Local.json).
**Инвариант:** смена `ExecutionEnvironment` при существующих чатах запрещена. Токен подписки
(`CLAUDE_CODE_OAUTH_TOKEN`) в песочницу доставляется per-exec (`DockerProcessRunner.BuildTurnEnv`
на каждый `docker exec`), а не запекается при создании контейнера.
**Перед правками в `Services/Execution/`, `SandboxManager`, `UserHomeResolver` — прочитай
[docs/architecture/sandbox.md](docs/architecture/sandbox.md)** (монтирования, interrupt, MCP из песочницы, overrides).

## Архитектура

```
Browser (React 18 + TypeScript)
    │ SignalR WebSocket
    ▼
ASP.NET Core 10 (:5000)
 ├── Controllers/
 │    ├── AuthController      POST /api/auth/ping
 │    ├── ProjectsController  CRUD /api/projects
 │    ├── SessionsController  /api/projects/{id}/sessions
 │    └── FilesController     /api/projects/{id}/files/*
 ├── Hubs/SessionHub          SignalR /hubs/session
 ├── Services/
 │    ├── ProjectManager      in-memory + data/projects.json
 │    ├── SessionManager      реестр сессий + IHubContext broadcast
 │    ├── Llm/                слой LLM-провайдеров (см. раздел «LLM-провайдеры»)
 │    │    ├── LlmProviderRegistry   CLI-провайдеры из конфига (env, цены, баланс)
 │    │    └── Claude/ClaudeSession  Process-обёртка claude.exe (единый рантайм)
 │    └── FileService         файловый менеджер (SafeJoin защита)
 └── Protocol/ServerMessage   record-типы WS-событий

Frontend (src/):
  pages/      LoginPage, ProjectListPage, WorkspacePage
  components/ ChatPanel, Composer, SessionList, FileExplorer,
              FileViewer, EmptyState, StatusBadge, NewSessionDialog
  hooks/      useSession (SignalR + ChatItem state)
  lib/        api.ts (REST), signalr.ts, design.ts (цветовые токены)
  types/      index.ts (Project, Session, ChatItem, ServerMessage, AuthState)
```

## Дизайн-макеты

Claude Design проект: `52adb1f7-312b-4f25-8c47-2bccfca9df94`

Ключевые файлы:
- `Claude Code Desktop.dc.html` — десктопные макеты (все состояния)
- `shots/desktop-files.png`, `shots/01-desktop-file.png`, `shots/02-desktop-file.png` — скриншоты

## Дизайн-система

**Полная конвенция — [docs/design/guidelines.md](docs/design/guidelines.md). Обязательна
для ЛЮБЫХ изменений UI** — гайд и есть общая канва, по которой идут все дальнейшие
доработки. Выжимка железных правил:

**Живой эталон UI-кита** — витрина: в dev открывается по `#/ui-kit` (без авторизации),
исходник [UiKitPage.tsx](frontend/src/dev/UiKitPage.tsx). Показывает все токены и
примитивы в одном месте — при UI-задачах читай его как референс правильного применения
дизайн-системы (токены, паттерны, состав примитивов). В production-бандл не попадает.

- Цвета — только токены `C.*` из [design.ts](frontend/src/lib/design.ts) (CSS-переменные,
  тёмная тема бесплатно). Сырой hex в `.tsx` — дефект; значения тем живут в
  [theme.css](frontend/src/lib/theme.css) (новый цвет — в ОБЕ темы). Текст на заливке
  токеном — `C.onAccent`, на подложке вне темы (палитра, фото, лайтбокс) — `C.onDark`.
- **Проверяется линтом:** `cd frontend; npm run lint:design` обязан быть зелёным (правило
  `design/no-raw-color`; исключения — список `RAW_COLOR_ALLOWED` в eslint.config.js либо
  построчный `eslint-disable-next-line` с причиной). Гейт стоит **перед коммитом**, а не
  после каждой правки: щиток судит только цвета и на изменениях раскладки, логики или
  замене токена на токен ловить ему нечего. По ходу работы хватает `npx tsc -b`.
- Размеры — из шкал: `FS` (шрифты), `SP` (отступы), `R` (радиусы), `SHADOW`, `Z`, `MODAL_W`.
- Контролы — только из `frontend/src/components/ui/` (Button, Modal, Field, Menu, Toggle,
  Island/IslandScaffold…) плюс общие Toolbar/EmptyState. Самодельные кнопки/модалки из
  div — дефект; не хватает примитива — расширь его или заведи в `ui/`.
- Accent-дисциплина: оранжевый `C.accent` — только главное действие и активные состояния.
- Шрифты: PT Serif (заголовки), Hanken Grotesk (UI), JetBrains Mono (код). Иконки —
  lucide-react + `ICON_SIZE`.
- Стили: только inline-objects, без Tailwind/CSS-modules.
- Каждый экран живёт на мобиле (`useIsMobile`, шторки вместо модалок).
- Новый раздел строится по «Рецепту нового раздела» из гайда (эталон — KnowledgePage);
  для заметного UI перед коммитом **предложить** прогон через субагента `designer`
  и дождаться ответа — сам, без спроса, он не запускается.

## LLM-провайдеры (Services/Llm)

Единственный рантайм — claude CLI (`Llm/Claude/ClaudeSession`); сторонние провайдеры
(DeepSeek, GLM) подключаются env-оверрайдами процесса на каждый ход (`ANTHROPIC_BASE_URL`,
`ANTHROPIC_AUTH_TOKEN`, модельные переменные). Конфиг — секция `LlmProviders`; ключи —
в appsettings.Local.json (пустой `ApiKey` = провайдер выключен). Резолв провайдера, цены
и возможности — `LlmProviderRegistry`. Инварианты:

- Смена провайдера у начатой сессии — 400 (транскрипт живёт у эндпоинта).
- Профили CLI `data/claude-profiles/{key}` изолируют OAuth-логин ~/.claude (иначе CLI
  пошлёт чужому эндпоинту токен подписки → 401); креденшалы в профили не копируются никогда.
- Иммунитет к системному окружению: на КАЖДОМ запуске claude из унаследованного env
  вычищаются `ANTHROPIC_*`, `CLAUDE_CONFIG_DIR` и пр. (`ProviderEnvKeys` → `ClearEnv`) —
  маршрут CLI задаёт только сервер; `CLAUDE_CODE_OAUTH_TOKEN` не трогается.
- One-shot вызовы — всегда `--safe-mode` + `--no-session-persistence` (авто-деградация,
  если CLI флага не знает); состав флагов — `OneShotClaudeRunner.BuildArgs` (под тестом).
- **Три слота моделей + таблица назначений.** Слоты **двухуровневые**: личный per-user слот
  (`User.ModelTierStrong|Medium|Weak`) поверх глобального инстанса
  (`AppSettings.ModelTierStrong|Medium|Weak`, уровень 2 диалога «Поставщики моделей»).
  Каждое МЕСТО применения модели — строка каталога `LocalActionCatalog` со значением
  «слот | конкретная модель | локальная (только фон)» в `LocalActionOverridesStore`
  (`tier:strong|medium|weak`; легаси `claude`/`default` ≙ средняя). Таблица назначений
  **глобальная** (админ решает, каким слотом идёт место), но слот в ней разрешается в модель
  **по владельцу действия** через `UserModelTierResolver.ModelFor(tier, ownerId)` —
  единственную точку склейки (ею пользуются и `ModelAssignmentResolver`, и `CheapTextRunner`;
  дублировать её логику нельзя). Агентные места (`Agentic: true`, группа «Чаты и персоны»):
  `chat-new`, `chat-persona`, `tasks-executor`, `subagent-consultant`, `modules-llm` — им
  локаль и `direct:`-модели недоступны, резолвит `ModelAssignmentResolver`; фоновым —
  `CheapTextRunner` по маршруту. Пустая модель сущности (`Session.Model`, `Persona.Model`) =
  «по назначению места»; явная модель и пины пантеона сильнее. **Уровень (тир) можно задать
  точечно** — `TaskItem.ModelTier` и `Persona.ModelTier` (`strong|medium|weak`, пусто = место):
  у исполнения задачи порядок `задача → Persona.Model → Persona.ModelTier → место`
  (`TaskExecutionService.ResolveExecutorModel`), у чатов персоны, групповых чатов, смены
  спикера и `PersonaAskService` — `Persona.Model → Persona.ModelTier → место`
  (`ModelAssignmentResolver.PersonaModel`, владельца резолвить только через
  `SessionManager.ResolveOwnerId`). Значения приходят и из MCP от LLM-постановщика, поэтому
  разбор — белый список трёх имён (`ModelTiers.TryParse`), не `Enum.TryParse`.
  Шлюз на границе запуска — `ClaudeSession.EffectiveModel` (ключ места по сессии) и
  `OneShotClaudeRunner.ResolveModel` (резолв ДО `BuildCliEnv`, `NormalizeModel` — ПОСЛЕ).
  Значение в сессии не фиксируется: `Model = null` резолвится каждый ход, смена настройки
  подхватывается сама. Заданный уровень — исключение: он разворачивается в модель при
  создании сессии (`SessionManager.ResolveDefaultModel`) и дальше живёт как обычная явная
  модель чата, поэтому смена слота задним числом на уже начатый чат не влияет.

Фоновые one-shot действия (теги, сводки, память, changelog…) считаются дёшево по цепочке
«выбранное → локальная Ollama → claude» (`LocalActionRouter` + `CheapTextRunner`, каталог —
`LocalActionCatalog`, `direct:`-модели OpenRouter — прямой HTTP-адаптер `CloudCheapClient`);
исполнителя каждого места выбирает админ в диалоге «Поставщики моделей» (уровень 3
«Кто что выполняет»), выбор действует сразу. Дефолтный маршрут — слот по профилю сложности
(Small/Text → слабая, Large → средняя); зашитый в потребителе тир (обычно `haiku`) остаётся
фолбэком на случай пустого слота. **Перед правками в `Services/Llm/` — прочитай
[docs/architecture/llm-providers.md](docs/architecture/llm-providers.md).**

## Claude Code CLI subprocess

`ClaudeSession` запускает: `claude --print --output-format stream-json --input-format stream-json --include-partial-messages --permission-prompt-tool stdio [--resume <id>]`

WorkingDirectory = `project.RootPath`

**stream-json → WebSocket маппинг:**
- `system { session_id }` → `session_started`
- `assistant text_delta` → `text_delta`
- `assistant thinking` → `thinking_delta`
- `assistant tool_use` → `tool_use`
- `user tool_result` → `tool_result`
- `sdk_control_request` → `permission_request` (ждём → пишем `control_response` в stdin)
- `result` → `result` + `exited`

## MCP-серверы продукта (mcp/*)

Каждый — один файл `mcp/{имя}-server/index.js`: чистый Node, stdio JSON-RPC, **без
зависимостей**. Подключение per-ход: `ClaudeSession.BuildTurnMcpConfig` собирает временный
MCP-конфиг и передаёт env (`*_API_URL`, сервисный JWT владельца, `*_PROJECT_ID`) + подсказку
в системный промпт; данные per-owner — токен ограничивает доступ. **Правило именования
инструментов:** не плодить однокоренные имена с пересекающейся семантикой (`execute` vs
`complete` — LLM путает; спутает человек в списке из 10+ — спутает и LLM).
**Инвариант: состав `tools/list` не зависит от хода** — он входит в сигнатуру запуска CLI,
и любая его зависимость от свойств хода (глубина делегирования, текст, флаги) перезапускает
процесс со всеми MCP-серверами: «Stream closed» на незавершённых вызовах и «No such tool
available». Ограничения по ходу — на бэкенде: серверы шлют `X-Caller-Session-Id`, экшены
помечены `[DenyOnDelegatedTurn]`; сторож — `McpToolsetStabilityTests`. **Состав режется по
фактическому спросу:** редко звавшиеся наборы уходят за tool-ключ с дефолтом «выключено», а не
удаляются (модуль заметок `notes-annotations`, запись истории `git_write` в wsp, уведомления
по роли), плюс потолок выдачи `files_read` — раздел «Диета состава по данным использования»
в доке ниже. **Диагностика:**
`GET /api/mcp/calls` (админ) — счётчики вызовов, доля отказов и последние сбои по каждому
инструменту (заголовки `X-Caller-Session-Id` + `X-Mcp-Tool` → `McpCallLogMiddleware`). Состав
инструментов, живучесть stdio-цикла и грабли HTTPS-деплоя («fetch failed» у всех инструментов
при живом бэкенде → явный `McpTasksApiUrl`) — [docs/architecture/mcp-servers.md](docs/architecture/mcp-servers.md).

## Заметки и Знания (Dify RAG)

Заметки — Obsidian-совместимый markdown-vault (`[[wikilinks]]`, backlinks, граф): настоящие
`.md` в личном vault `data/notes/{userId}` + `notes/` проектов; бэкенд `NotesService`,
MCP notes-server; семантика — Dify-датасет `{username}:notes` (без `Dify:ApiKey` тихо
выключена). Знания — менеджер Dify-датасетов (`KnowledgeBasesController`; Dify — источник
истины, классификация по имени датасета; каждый `{id}`-эндпоинт проверяет релевантность
юзеру, иначе 403). Файлы проектов синкаются в датасеты дифф-по-хешам с дебаунсом 15с
(`ProjectKnowledgeSyncService`) + lifecycle-каскады (удаление проекта/юзера, смена RootPath).
Контуры Dev/Prod на одном Dify разводит `Dify:Namespace`. **Перед правками — прочитай
[docs/architecture/knowledge.md](docs/architecture/knowledge.md).**

## Интеграция с мессенджерами (Max / Telegram) — не реализовано

Исследование (июль 2026): Max Bot API зрелый (REST, webhook'ы, inline-кнопки, mini-apps),
но C# SDK нет — писать свой клиент. География — только РФ, модерация ботов обязательна.
**Сценарий, оправдывающий интеграцию:** CCS крутится на сервере, юзер не за компьютером,
нужно знать о завершении задач или реагировать на permission-запросы через мессенджер.
Полноценный чат с Claude через мессенджер делать **не надо** — мессенджер не отрендерит
diff/артефакты/виджеты, это убивает UX CCS.

Use cases (с резолюциями), архитектура интеграции (куда встраивать: webhook-контроллер
по образцу `PersonaAutomationService`, `IMessengerClient` для нескольких адаптеров, mapping
`external_chat_id ↔ session_id`) и расширенные варианты использования (личка с глобальной
персоной, проект-специфичные чаты, уведомления от имени персон — через `Session.PersonaId`
и `Session.ProjectId` как оси routing'а) — [docs/research/messenger-integration.md](docs/research/messenger-integration.md).
**Решение по ботам:** один бот с routing по чатам для solo (CCS-уведомления + телеметрия,
см. [observability.md](docs/observability/overview.md) «Future Epics — Alerting»); переход на двух
ботов при появлении второго юзера или шумной телеметрии. Общая .NET-библиотека Max API
клиента — переиспользуется между CCS и сервисом телеметрии.

## Персоны

«Персоны = контакты, Чаты = разговоры»: персона — отдельная per-owner сущность
(`data/personas.json`, не .md-агент) с ролью/характером/аватаром/моделью/зоной/долгой
памятью. Чат с персоной = `Session.PersonaId`: слой персоны (контракт
`PersonaPromptBuilder` + recall памяти) пересобирается каждый ход и переживает рестарт;
зона определяет scope чата (глобальная → вне проекта, проектная → её проект). Память —
`data/persona-memory.json` + семантический слой в Dify; auto-recall в промпт каждого хода.
Инварианты:

- У задач `PersonaId != null ⇒ Assignee = Claude` (`TaskManager.NormalizePersonaAssignee`).
- Доступы: `Persona.Access` (full/readOnly/custom) → `PersonaAccessPolicy` формирует
  disallowed-инструменты; `Persona.Tools` гейтит tasks/notes/web.

Флаги: `personas`, `persona-memory-autolearn`, `persona-memory-consolidation`,
`persona-mentions`, `persona-group-chats`. **Перед правками в персонах (промпт, память,
групповые чаты, пантеон OmO, аватары, MCP personas/memory) — прочитай
[docs/architecture/personas.md](docs/architecture/personas.md).**

## Механики OmO в чатах

Тексты — переводы oh-my-openagent ([docs/omo/adoption.md](docs/omo/adoption.md)); рантайм —
`Services/Prompts/OmoPrompts*.cs` (генерируются скриптом docs/omo/gen-omo-prompts.ps1).
Главное — цикл «до готово» (флаг `work-loop`): тумблер в композере, протокол маркера
`<promise>ГОТОВО</promise>`, автопродолжение хода до маркера/лимита, затем верификационный
ход. Детали — [docs/architecture/features.md](docs/architecture/features.md), раздел «Механики OmO».

## REST API

Все эндпоинты (кроме `/api/auth/ping`) и SignalR-хаб — под `[Authorize]` (API-ключ);
`ping` дополнительно под rate-limit (`Auth:PingRateLimit`, дефолт 10/мин на IP).
Полный справочник эндпоинтов — [docs/architecture/api.md](docs/architecture/api.md) (источник правды — контроллеры).
Значения фич-флагов фронт получает из `GET /api/auth/me` (поле `featureFlags`).
Удалённый доступ — [docs/operations/remote-access.md](docs/operations/remote-access.md).

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

## Observability (OpenTelemetry)

Двухрежимная observability через OTel SDK: **dev** → Aspire Dashboard (in-memory, для
живого дебага), **production** → SigNoz (ClickHouse, persistent 30d traces / 90d metrics).
**Алерты** доставляются в уведомления CCS (колокол + тост + web push на PWA, категория
«Алерт»): `AlertPollingService` раз в 60с опрашивает `GET /api/v1/alerts` SigNoz и шлёт
админам новые срабатывания через существующий `NotificationService`. Опрос, а не webhook —
боевой хост слушает HTTPS с сертом на домен, и запрос из контейнера падает по SNI.
Правила — код (`docker/observability/alerts/*.json` + `apply-alerts.ps1`), дедупликация
по `fingerprint` (одно правило = по алерту на серию разреза), состояние —
`data/alert-state.json`. Рассылает только инстанс с `Telemetry:Alerts:Enabled` (подписки
per-инстанс). Страховка на случай мёртвого CCS — email-канал самого SigNoz для правила
«Пульс телеметрии пропал». **Max для ботов закрыт** (только верифицированные юрлица РФ) —
см. [docs/research/messenger-integration.md](docs/research/messenger-integration.md).
**Раздел «Телеметрия» в UI** (меню аватара, admin-only): SigNoz встроен `<iframe>` через
same-origin проброс `/telemetry-proxy/**` (middleware в Program.cs, cookie `cc_telemetry`,
роль по `UserStore`). SigNoz релоцирует SPA под префикс через env `SIGNOZ_GLOBAL_EXTERNAL__URL`
(overlay, переменная `SIGNOZ_EXTERNAL_URL`); фронт решает iframe/заглушку по
`GET /api/telemetry/status`. Включение — `Telemetry:Ui:Enabled`.

- **Central doc**: [docs/observability/overview.md](docs/observability/overview.md) — scope, архитектура,
  дублирование с существующими сторами, privacy (PII sanitizer), cardinality guardrails,
  sampling strategy, future epics.
- **Audit**: [docs/observability/audit.md](docs/observability/audit.md) — карта
  существующих observability-поверхностей (SpendStore JSONL, ModuleLlmUsageStore,
  McpCallLog in-memory, ProjectEventLogService SQLite). **SpendStore = source of truth
  для billing (токены/стоимость), OTel метрики НЕ дублируют его.**
- **SigNoz setup**: [docs/observability/signoz-setup.md](docs/observability/signoz-setup.md) —
  развёртывание vendored SigNoz v0.134.0 (`docker/observability/`), retention, backup,
  troubleshooting.

Включение per-instance через `appsettings.Local.json` секция `Telemetry`. Все порты
(SigNoz UI :3301, OTLP :4317/4318) bind'ятся к `127.0.0.1` через overlay
`docker-compose.observability.yml`. PII-санитайзер (`PiiSanitizingProcessor`) сидит первым
в pipeline — оба backend'а получают очищенные данные. **Перед правками в `Telemetry/`
или новыми метриками — прочитай [docs/observability/overview.md](docs/observability/overview.md).**

## Реализовано

Ядро: auth по API-ключу, проекты, сессии, чат (вложения/голос/режимы ⚡📋❓), файловый
менеджер с diff/revert, empty states. Поверх ядра: виджеты в чате (sandbox-iframe + строгая
CSP), артефакты сессии (панель, derived из ленты), продуктовая история «Что нового»
(AI-сводка коммитов по дням), плагин oh-my-claudecode (авто-установка + роутинг персон
в сабагенты), задачи v3 (напоминания, регулярные, web push, Claude/персона-исполнитель),
бэкапы каталога `data` (расписание + `exe --backup/--restore/--inspect`, меню трея, виджет
на главной; настройка — секция `Backup` в `appsettings.Local.json`), панель «Документация»
(документация проекта настраиваемой областью — файлы корня + папки + типы файлов, дефолт `README.md` + `docs/**` + Markdown
как связный корпус: дерево, оглавление, поиск,
переходы по ссылкам, обратные ссылки, отправка документа или раздела в чат).
Детали каждой фичи — [docs/architecture/features.md](docs/architecture/features.md).

## Фич-флаги (feature toggles)

Dark launch: фича коммитится выключенной и включается per-user в меню «Экспериментальные
функции». Реестр (source of truth) — в коде: `FeatureFlagCatalog.All`
([Models/FeatureFlag.cs](backend/ClaudeHomeServer/Models/FeatureFlag.cs)); хранение —
override в `data/users.json`; фронт — стор [lib/featureFlags.ts](frontend/src/lib/featureFlags.ts),
хук `useFeature(FLAGS.key)`. Каталог опустошён (2026-07): все старые флажные фичи включены
безусловно, пометки «за флагом …» в доках — исторические; актуальный состав — в коде каталога.

**Как добавить новый флаг (3 шага):**
1. Бэк: добавить строку в `FeatureFlagCatalog.All` (`key`, `title`, `description`, `Default: false`, `stage`).
2. Фронт: добавить ключ в const `FLAGS` в `lib/featureFlags.ts`.
3. Обернуть фичу: `{ useFeature(FLAGS.myFeature) && <MyFeature /> }`.

Тумблер в модалке появится сам (рендерится из каталога). Ключи дублируются в двух местах
(C#-каталог и TS-`FLAGS`) — при переименовании править оба.

## Агенты (.claude/agents/)

Содержимое папки в .gitignore: туда `PersonaAgentFileSync` синкает персон-консультантов
владельца (у каждого пользователя свои, между машинами конфликтуют). Версионируются только
общие проектные агенты — точечные `!`-исключения в .gitignore:

| Агент | Роль |
|---|---|
| `designer` | ревью UI-изменений по [docs/design/guidelines.md](docs/design/guidelines.md); запускается только по явному согласию — перед коммитом заметного UI его предлагают, а не вызывают молча |

## Конфигурация

Машинно-специфичные значения (локальные пути `DefaultProjectsPath`/`McpConfigPath`,
секреты, локальные URL) **не правим в отслеживаемых `appsettings*.json`** — там лежат
общие дефолты. Свои значения кладём в `backend/ClaudeHomeServer/appsettings.Local.json`
(в `.gitignore`, не коммитится, у каждого свой). Образец —
`appsettings.Local.example.json`: скопировать в `appsettings.Local.json` и вписать своё.

Порядок загрузки (последний переопределяет): `appsettings.json` →
`appsettings.{Environment}.json` → `appsettings.Local.json`. Подключается в
[Program.cs](backend/ClaudeHomeServer/Program.cs) сразу после `CreateBuilder`.

## Соглашения

- **CI гоняет тесты на Linux** (`ubuntu-latest`, [.github/workflows/ci.yml](.github/workflows/ci.yml)),
  а разработка идёт на Windows — тесты обязаны быть платформонезависимыми, иначе зелёные
  локально они падают в CI. Главная ловушка — пути: `Path.IsPathRooted("C:\\…")` на Linux
  даёт `false`, поэтому Windows-литералы там считаются относительными и проверки путей
  срабатывают не по тому правилу. Пути в тестах строить от `Path.GetTempPath()` +
  `Path.Combine`, разделители не хардкодить. Помнить и про остальное: регистрозависимость
  ФС, отсутствие `.exe`, недоступность WinAPI. Сомневаешься — прогони набор в контейнере:
  `docker run --rm -v "<репа>:/src" -w /src/backend mcr.microsoft.com/dotnet/sdk:10.0 dotnet test ClaudeHomeServer.Tests/ClaudeHomeServer.Tests.csproj`
  (после этого пересобери локально: контейнер оставляет в `bin`/`obj` Linux-артефакты)
- **Тесты и их категории.** Большинство — чистые юнит-тесты (Services, моки, in-memory),
  1–50ms. Медленные — две группы: **Controllers** (поднимают `WebApplicationFactory` —
  полный HTTP-pipeline ASP.NET, 400–970ms) и **GitServiceTests** (гоняют настоящий `git`
  CLI во временных репо, помечены `[Trait("Category", "Slow")]`, 500–720ms). Интеграционные MCP
  — HTTP-вызовы к MCP-серверам, 400–800ms. На итеративную правку гоняй только релевантный
  набор: `dotnet test --filter "FullyQualifiedName~SessionManagerTests"`; медленные категории
  (Controllers, GitServiceTests, интеграционные MCP) запускай только при правках в коде, который
  они покрывают, либо при действительной необходимости — полный прогон перед коммитом/PR.
- Хранилище проектов: `data/projects.json` рядом с executable
- **Одна папка — один проект на владельца**: `RootPath` нормализуется при создании и смене папки
  (`Path.GetFullPath` — схлопывает двойные разделители), а `ProjectManager.EnsureRootFree`
  отклоняет повторное подключение той же папки (400 «Эта папка уже подключена как проект …»).
  Причина: датасет знаний в Dify и запись `WorkspaceKnowledge` ключуются по `RootPath` —
  проекты-близнецы спорили бы за одну базу. У **разных** владельцев общая папка допустима:
  на этом держатся каскады «соседей по папке» (`GetByRootPath`)
- Метаданные сессий персистятся в `data/sessions.json`, история чата — `data/sessions/{claudeSessionId}/history.json`; процессы claude in-memory, resume через `--resume <claude-session-id>`
- **Удаление чата уносит и транскрипт claude CLI** (`{профиль}/projects/{уплощенный cwd}/{csid}.jsonl`
  плюс одноименную папку сабагентов): `SessionManager.DeleteTranscript` → `TranscriptMigrator.DeleteEverywhere`.
  Ищем во ВСЕХ профилях (`LlmProviderRegistry.GetAllConfigRoots` + `sandbox-profiles/{ownerId}/*`),
  потому что переезды между профилями (`TryMigrate`) и рабочими папками (`TryRelocateCwd`) намеренно
  оставляют копии. Иначе переписка удаленного чата жила на диске до плановой уборки CLI (~30 дней).
  **Инвариант:** удаляется только файл с точным именем `{csid}.jsonl` — никогда по маске, по времени
  и никогда сама папка. Один `~/.claude` делят dev-, прод- и контейнерный инстансы плюс интерактивные
  сессии самого пользователя (ее слаг совпадает со слагом проекта, подключенного в CCS!), так что
  «вычистить лишнее из папки» = снести чужую историю. Сборщика «сирот» по этой же причине нет.
  **Гейт общего разговора:** сессия, созданная с `resumeSessionId`, несет ТОТ ЖЕ `ClaudeSessionId`,
  поэтому история и транскрипт удаляются только когда на них не ссылается другой живой чат —
  иначе у него пропадала лента (это баг, описанный в `WorkspacePage.tsx`) и вся память `--resume`.
  **Валидация:** `resumeSessionId` из тела запроса садится в `ClaudeSessionId`, а тот становится
  именем папки `data/sessions/{csid}` и файла транскрипта, которые удаляются рекурсивно — поэтому
  `StartNewSessionAsync` пропускает только `^[A-Za-z0-9_-]{1,128}$` (`TranscriptMigrator.IsSafeSessionId`),
  иначе 400. Без гейта `resumeSessionId: ".."` означал бы `Directory.Delete` всей папки `data`.
  Проверка на белом списке, а не через `Path.GetFileName`: тот пропускает `.` и `..`, а на Linux
  еще и `..\..\` целиком (обратный слеш там — легальный символ имени)
- Временные чаты: `Session.ExpiresAfterMinutes` (null — обычный чат), тумблер + пресеты срока в «Настройках чата»; `ChatExpiryService` (тик 60с) удаляет чаты, неактивные дольше срока (кроме статусов Working/Waiting); `DeleteAsync` чистит историю на диске и шлёт `chat_deleted`
- Path traversal защита: `FileService.SafeJoin` — все пути через неё
- git diff/revert через `git` CLI; если не git-репо — возвращает null
- **Новое хранилище → сверься с бэкапом.** Всё в `data/` попадает в архив по умолчанию
  (`BackupPaths.ShouldInclude` работает от обратного — исключениями), поэтому думать надо,
  когда добавляешь: (1) **кеш, лог или временный файл** в `data/` → добавь в исключения,
  иначе мусор поедет в облако; (2) **секрет или ключ** → в `BackupPaths.SecretFileNames`,
  иначе он уедет в облачную папку вместе с архивом; (3) **хранилище ВНЕ `data/`** (как
  профили CLI или корень песочницы) → реши явно, бэкапить ли, и допиши в
  `BackupCore.CopyDataTo`; (4) **не-JSON стор** (вторая БД и т.п.) → копирование файлом
  может дать протухший снимок, нужен свой способ (у SQLite — `BackupDatabase`);
  (5) **критичный стор** → добавь в `BackupValidation.Validate`, иначе его порча не
  остановит восстановление. Ломающее изменение формата любого стора = инкремент
  `BackupSchema.Version` (иначе старый код молча обнулит стор при откате).
  Детали — [docs/architecture/features.md](docs/architecture/features.md#бэкапы-и-восстановление)
- Комментарии в коде по-русски

## Коммиты

- **Conventional Commits**: `type(scope): описание` (feat/fix/perf/docs/refactor/build/chore/ci/test/style).
- **Язык сообщений — русский** (в отличие от общего дефолта на английском).
- Трейлер `Co-Authored-By: <модель> <noreply@<домен-вендора>>` — где `<модель>` это
  та, что реально делала коммит (напр. «Claude Opus 4.8», «GLM 5.2»), а не фиксированная
  версия. Домен noreply берётся по вендору модели: Anthropic → `noreply@anthropic.com`,
  ZhipuAI (GLM) → `noreply@z.ai`. Без «Claude» в начале, если модель не от Anthropic.
- Атомарность: одно логическое изменение — один коммит.
- `commit`/`push` — только по явной просьбе.
