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
# Vite (:5173) проксирует /api и /hubs (WebSocket) на :5000. Стенд :5000 раздаёт именно
# frontend/dist ПОСЛЕДНЕЙ сборки: после правок .tsx нужен npm run build (или сиди на :5173);
# wwwroot в репо не живёт — прод получает его из dist агентом выкатки. Детали: docs/operations/dev-stand-host.md
```

Хостовый дев-стенд поднимаем **только через `dotnet run`** (или с явным
`ASPNETCORE_ENVIRONMENT=Development`): порождённые процессы наследуют `Production`, а там
`Kestrel:Endpoints` уводит стенд на занятый боевым инстансом :80, и `ASPNETCORE_URLS` это не
чинит. Разбор и команда фонового запуска — [docs/operations/dev-stand-host.md](docs/operations/dev-stand-host.md).

## Среда исполнения пользователей (local / container)

Изоляция per-**пользователь**: `User.ExecutionEnvironment` = `local` (процессы на машине
сервера) | `container` (общая docker-песочница `cc-sandbox`); бэкенд всегда НА ХОСТЕ (Windows).
Все точки запуска процессов идут через `IProcessLauncher` / `ILauncherFactory.ForOwner(ownerId)`
(драйверы `LocalProcessRunner` / `DockerProcessRunner`); системные one-shot — всегда local.
Пути: бэкенд работает ТОЛЬКО с хостовыми, `IPathMapper` переводит в контейнерные в момент
запуска. Домашние папки юзеров — единая точка
[UserHomeResolver.cs](backend/ClaudeHomeServer/Services/UserHomeResolver.cs).

**Инварианты:** смена `ExecutionEnvironment` при существующих чатах запрещена; токен подписки
доставляется в песочницу per-exec, а не запекается при создании контейнера.

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

Claude Design проект: `52adb1f7-312b-4f25-8c47-2bccfca9df94`. Ключевые файлы:
`Claude Code Desktop.dc.html` (десктопные макеты, все состояния), `shots/*.png`.

## Дизайн-система

**Полная конвенция — [docs/design/guidelines.md](docs/design/guidelines.md), обязательна
для ЛЮБЫХ изменений UI.** Живой эталон — витрина UI-кита: в dev по `#/ui-kit` без
авторизации, исходник [UiKitPage.tsx](frontend/src/dev/UiKitPage.tsx) (в production-бандл
не попадает). Железные правила:

- Цвета — только токены `C.*` из [design.ts](frontend/src/lib/design.ts); сырой hex в `.tsx` —
  дефект; значения тем — в [theme.css](frontend/src/lib/theme.css) (новый цвет — в ОБЕ темы).
- **Проверяется линтом:** `cd frontend; npm run lint:design` обязан быть зелёным. Гейт стоит
  **перед коммитом**, а не после каждой правки; по ходу работы хватает `npx tsc -b`.
- Размеры — из шкал `FS`, `SP`, `R`, `SHADOW`, `Z`, `MODAL_W`.
- Контролы — только из `frontend/src/components/ui/`; самодельные кнопки/модалки из div — дефект.
- Accent-дисциплина: оранжевый `C.accent` — только главное действие и активные состояния.
- Шрифты: PT Serif (заголовки), Hanken Grotesk (UI), JetBrains Mono (код); иконки — lucide-react.
- Стили: только inline-objects, без Tailwind/CSS-modules. Каждый экран живёт на мобиле (`useIsMobile`).
- Новый раздел — по «Рецепту нового раздела» из гайда (эталон — KnowledgePage); для заметного
  UI перед коммитом **предложить** прогон через субагента `designer` и дождаться ответа.

## LLM-провайдеры (Services/Llm)

Единственный рантайм — claude CLI (`Llm/Claude/ClaudeSession`); сторонние провайдеры
(DeepSeek, GLM) подключаются env-оверрайдами процесса на каждый ход. Конфиг — секция
`LlmProviders`, ключи в appsettings.Local.json (пустой `ApiKey` = провайдер выключен);
резолв, цены и возможности — `LlmProviderRegistry`. Инварианты:

- Смена провайдера у начатой сессии — 400 (транскрипт живёт у эндпоинта).
- Профили CLI `data/claude-profiles/{key}` изолируют OAuth-логин ~/.claude (иначе CLI пошлёт
  чужому эндпоинту токен подписки → 401); креденшалы в профили не копируются никогда.
- Иммунитет к системному окружению: на КАЖДОМ запуске из унаследованного env вычищаются
  `ANTHROPIC_*`, `CLAUDE_CONFIG_DIR` и пр. (`ProviderEnvKeys` → `ClearEnv`); маршрут CLI задаёт
  только сервер, `CLAUDE_CODE_OAUTH_TOKEN` не трогается.
- One-shot вызовы — всегда `--safe-mode` + `--no-session-persistence` (состав флагов —
  `OneShotClaudeRunner.BuildArgs`, под тестом).
- **Три слота моделей (strong/medium/weak) + глобальная таблица назначений.** Слот личный
  per-user поверх глобального инстанса; каждое МЕСТО применения модели — строка каталога
  `LocalActionCatalog`. Слот разрешается в модель **по владельцу действия** через
  `UserModelTierResolver.ModelFor(tier, ownerId)` — единственную точку склейки, дублировать её
  логику нельзя. Агентным местам (`Agentic: true`) локаль и `direct:`-модели недоступны.
  Приоритет: явная модель сущности → её `ModelTier` → назначение места. Шлюзы на границе
  запуска — `ClaudeSession.EffectiveModel` и `OneShotClaudeRunner.ResolveModel`.
- **Фолбэк хода — только по цепочке**, автоподбора нет: ротация подписок пула (тихо) → шаги
  цепочки (маркер в ленте) → честная ошибка. Классы ошибок (`TurnErrorClassifier`): RateLimit /
  UsageLimit / ProviderError / Unreachable / ContextOverflow / **AuthFailure**; `None` —
  неопознанная/содержательная ошибка: фолбэк НЕ запускается, но ход обязан завершиться `error`
  (не штатным finished). AuthFailure (401/протухший OAuth/ключ) в пуле Claude триггерит тихую
  ротацию уровня 1 (свой токен у другой подписки), у стороннего провайдера — шаг цепочки;
  пометка `MarkAuthDead` обратима: снимается по `rate_limit_event` от подписки (доказательство
  аутентификации) в обработчике хода и в идл-пинге warmup.
- **Красная карточка ошибки — только у хода, который реально не состоялся.** Промежуточная
  ошибка попытки задерживается вместе с её result: подмена состоялась → карточки нет,
  сырой текст сворачивается в «Подробности» маркера подмены; подмены не было → ошибка уходит
  строго перед финальным result. Тексты для человека — одна точка `Services/Llm/TurnFailureText`
  (сырой `ex.Message`/ответ CLI живёт в `ErrorMessage.Details` и в логе полным `{ex}`);
  классификатор работает с СЫРЫМ текстом, по русской формулировке 529 не распознаётся.

Фоновые one-shot действия (теги, сводки, память, changelog…) считаются дёшево по маршруту
`LocalActionRouter` + `CheapTextRunner`; исполнителя каждого места выбирает админ в диалоге
«Поставщики моделей», выбор действует сразу.

**Перед правками в `Services/Llm/` — прочитай
[docs/architecture/llm-providers.md](docs/architecture/llm-providers.md)**; слоты и таблица
назначений — [model-presets-and-tiers.md](docs/features/model-presets-and-tiers.md), цепочки
фолбэка и ёмкость контекста — [ADR-007](docs/adr/ADR-007-model-preset-chains.md) §4.

## Генератор картинок (Services/Images)

Аватар персоны рисует слой драйверов `IImageGenerator` (fal.ai — синхронный,
glif — `compose_project` + опрос джобы) за роутером `ImageGenerationService`. Провайдера
(Автоматически | fal.ai | glif) и модель выбирает админ **отдельно для каждого места**
(`ImagePlaces` — сейчас одно: `persona-avatar`) — секция «Картинки» вкладки «Применение»
(`GET/PUT /api/image-generation`, стор `data/image-generation.json` поверх секции конфига
`Images`). Модель выбирается только у fal; в «Автоматически» (порядок `glif` → `fal`) и у glif
её подбирает сам генератор. Инвариант тот же, что у моделей:
**явно выбранного провайдера не подменяем**, переход на другого — только в «Автоматически».
Не нарисовалось (сервис не настроен, отказ) — сущность живёт на инициалах, а картинку догоняет
очередь `ImageBackfillService` (`data/image-backfill.json`, событие `image_backfilled`).
Детали — [docs/features/image-generation.md](docs/features/image-generation.md).

## Значок проекта (Services/ProjectIcons)

Иконка проекта — **не картинка**: модель по названию проекта отдаёт имя иконки из белого
списка lucide (`LucideGlyphs`, серверная копия полного набора установленного lucide-react),
разметки от модели не приходит никогда. Подбор двухходовый («меню вместо памяти»): ход 1 —
слова-понятия, сервер отбирает по ним реальные имена, ход 2 — выбор из этого короткого меню;
ноль годных — ровно один повтор с перечислением отбракованных, без цикла. Место модели —
`project-icon` в `LocalActionCatalog`, любой сбой молча оставляет инициалы. Контракт ответа,
схема ходов, белый список и форма хранения — [ADR-009](docs/adr/ADR-009-project-icon-glyph.md);
тексты интерфейса — [docs/features/project-icon-glyphs.md](docs/features/project-icon-glyphs.md).

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
MCP-конфиг и передаёт env (`*_API_URL`, сервисный JWT владельца) + подсказку в системный
промпт; данные per-owner — токен ограничивает доступ.

- **Правило именования:** не плодить однокоренные имена с пересекающейся семантикой
  (`execute` vs `complete` — LLM путает).
- **Инвариант: состав `tools/list` не зависит от хода** — он входит в сигнатуру запуска CLI,
  и любая его зависимость от свойств хода перезапускает процесс со всеми MCP-серверами
  («Stream closed», «No such tool available»). Ограничения по ходу — на бэкенде
  (`X-Caller-Session-Id` + `[DenyOnDelegatedTurn]`); сторож — `McpToolsetStabilityTests`.
- Состав режется по фактическому спросу: редко звавшиеся наборы уходят за tool-ключ с дефолтом
  «выключено», а не удаляются.
- **Диагностика:** `GET /api/mcp/calls` (админ) — счётчики вызовов, доля отказов и последние
  сбои по каждому инструменту.

Состав инструментов, живучесть stdio-цикла и грабли HTTPS-деплоя («fetch failed» у всех
инструментов при живом бэкенде → явный `McpTasksApiUrl`) —
[docs/architecture/mcp-servers.md](docs/architecture/mcp-servers.md).

**Личный реестр MCP-серверов** (раздел «MCP-серверы») — внешние серверы владельца со статусом,
пробой, входом по OAuth и доступом по проектам/персонам. Каскад доступности — **allow-list,
единственная модель**: сервер не едет никуда, пока не включён в проекте чата ИЛИ выдан персоне
чата; чат вне проекта без персоны — по `McpServerRecord.AllowOutsideProjects`. Чистое OR-правило
— `McpDelivery.ShouldDeliver`. Известное ограничение: полный цикл входа по OAuth не проверялся на
реальном сервере. Подробности — [docs/architecture/mcp-registry.md](docs/architecture/mcp-registry.md).

## Заметки и Знания (Dify RAG)

Заметки — Obsidian-совместимый markdown-vault (`[[wikilinks]]`, backlinks, граф): настоящие
`.md` в личном vault `data/notes/{userId}` + `notes/` проектов; семантика — Dify-датасет
`{username}:notes` (без `Dify:ApiKey` тихо выключена). Знания — менеджер Dify-датасетов
(Dify — источник истины; каждый `{id}`-эндпоинт проверяет релевантность юзеру, иначе 403).
Файлы проектов синкаются дифф-по-хешам с дебаунсом 15с (`ProjectKnowledgeSyncService`) +
lifecycle-каскады. Контуры Dev/Prod на одном Dify разводит `Dify:Namespace`.
Документы, упавшие на индексации (статус `error`), лечит фоновый реконсайлер
(`Dify:Reconcile:Mode` = off | observe | heal, **дефолт off**): находит их у участников синка,
сбрасывает хеши — штатный синк пересоздаёт из источника истины; несопоставимые со сторами
(сироты, ручные документы) только показываются, не лечатся.
**Перед правками — прочитай [docs/architecture/knowledge.md](docs/architecture/knowledge.md).**

## Интеграция с мессенджерами (Max / Telegram) — не реализовано

Оправдывающий сценарий: CCS крутится на сервере, юзер не за компьютером, нужно знать о
завершении задач или реагировать на permission-запросы. Полноценный чат с Claude через
мессенджер делать **не надо** — он не отрендерит diff/артефакты/виджеты. **Max для ботов
закрыт** (только верифицированные юрлица РФ). Исследование, архитектура интеграции и решение
по ботам — [docs/research/messenger-integration.md](docs/research/messenger-integration.md).

## Персоны

«Персоны = контакты, Чаты = разговоры»: персона — отдельная per-owner сущность
(`data/personas.json`, не .md-агент) с ролью/характером/аватаром/моделью/зоной/долгой памятью.
Чат с персоной = `Session.PersonaId`: слой персоны (`PersonaPromptBuilder` + recall памяти)
пересобирается каждый ход и переживает рестарт; зона определяет scope чата. Инварианты:

- У задач `PersonaId != null ⇒ Assignee = Claude` (`TaskManager.NormalizePersonaAssignee`).
- Доступы: `Persona.Access` (full/readOnly/custom) → `PersonaAccessPolicy` формирует
  disallowed-инструменты; `Persona.Tools` гейтит tasks/notes/web.

**Перед правками в персонах (промпт, память, групповые чаты, пантеон OmO, аватары, MCP
personas/memory) — прочитай [docs/architecture/personas.md](docs/architecture/personas.md).**

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

## Выкатка на бой из веб-морды (`Services/Deploy`)

> **Не путать с выкаткой прода из чата** ([ADR-010](docs/adr/ADR-010-deploy-from-chat.md)):
> та живёт рядом, в том же `Services/Deploy`, но это другая механика — заявка в журнал и
> внешний агент планировщика (`DeployService`, роут `api/deploy`, секция конфига `Deploy`).
> Здешняя кнопка — сигнал трей-раннеру (`TrayDeployController`, роут `api/admin/deploy`,
> секция `TrayDeploy`).

Пункт меню аватара «Выкатить на бой» просит трей-раннер (соседний репозиторий
`ClaudeCodeServerRunner`) опубликовать рабочее дерево **как есть**: тот гасит продукт,
собирает, поднимает обратно, а если новая сборка не отвечает — сам возвращает предыдущую.
Смысл кнопки в том, что **выкатка перестаёт зависеть от LLM**: раньше её запускал только
личный скилл из чата, и кончившиеся лимиты провайдера означали «выкатить нельзя».

**Сигнал ставим сами, exe раннера не запускаем.** `DeployLauncher` открывает именованное
событие (`ITrayGate` → `WindowsTrayGate`) и ставит его — ровно то же делает `Program.Signal`
раннера. Соблазн запустить `ClaudeServerTray.exe --deploy-as-is`, как это делает скилл, надо
гасить на корню: при **мёртвом** трее такой запуск не «умирает за миллисекунды», а поднимает
полноценный трей, дочерний нашему процессу, — и тот при старте гасит «осиротевшие» инстансы
продукта по совпадению полного пути, то есть собственного родителя. Со ставкой события этого
класса сценариев нет вовсе, и заодно исчезает щель между «проверили живость» и «запустили»:
здесь проверка и есть сама операция.

**Замков четыре, и они независимы:** `TrayDeploy:Enabled` (false по умолчанию, живёт в машинном
`appsettings.Local.json` вне git), `[Authorize(Roles = "admin")]`, отказ при неоткрывшемся
событии и `AllowRemoteDeploy` на стороне раннера. Скрывать пункт в UI без серверной проверки
нельзя — веб-морда торчит наружу. Код уезжает всем, у кого свой инстанс и свой раннер, поэтому
защита обязана быть конфигурацией, а не отсутствием кода. Хэндл события **не кэшируем**:
именованный объект ядра переживёт смерть трея, и сохранённый хэндл сделал бы проверку вечно
успешной.

**Ответ уходит раньше сигнала** (`Response.OnCompleted`): продукт гаснет через секунду-другую
после события, и `202`, отданный после запуска, до браузера бы не доехал.

**Статус запрашивается флагом `live: true`** (`lib/offline.ts`) — то есть мимо офлайн-кэша.
Это не оптимизация, а условие работоспособности: офлайн-слой по умолчанию кладёт каждый GET в
IndexedDB и при обрыве **молча возвращает сохранённый ответ вместо ошибки**. Для статуса
выкатки это смертельно — продукт на время публикации гаснет, а окно получало бы «сервер
отвечает, ничего не изменилось» и объявляло «трей команду не принял» поверх успешной выкатки
(так и было 19.08, трижды). Здесь важно не только содержимое ответа, но и сам факт, что сервер
жив. Со стороны браузера то же закрывает `cache: 'no-store'` плюс `ResponseCache(NoStore)` на
эндпоинте.

**Как модалка отличает свою выкатку от чужого итога.** Раннер пишет `running` не сразу
(сперва git-проверки), а при отказе — например, когда удалённая выкатка выключена в его
конфиге — не перезаписывает файл **вовсе**. Поэтому `DeployModal` запоминает `startedAt`
прошлой выкатки как базис и считает итог своим, только если время изменилось. Второй признак
приёма команды — **обрыв связи**: сам себя сервер не гасит. Итог показывается лишь при
`startedAt != базис И result != running` — иначе краш продукта с подъёмом по watchdog или
обрыв сети у клиента показали бы `ok` от прошлой выкатки как успех текущей.
Значений `result` семь: `running`, `ok`, `blocked`, `build-failed`, `rolled-back`, `failed`,
`error` — формат чужой, читаем как есть.

Режим один — «как есть». Строгий режим раннера (`fetch` + `ff-only`) сюда не вынесен: он
отказывает на грязном дереве, а рабочая репа продукта почти всегда грязная — кнопка чаще
отвечала бы отказом, чем работала.

**Пока идёт выкатка, вместо окна показывается заставка** — та же `LoadingScreen`, что и при
старте приложения (у неё появились `overlay` и `children`). Причина не в красоте: продукт в это
время остановлен, любой запрос падает, и окно поверх мёртвого интерфейса врёт про доступность.
Заставку можно свернуть — выкатка занимает минуты, запирать в ней человека без выхода нельзя;
наблюдение при этом продолжается. Тем же экраном закрывается и переход на новую версию фронта.

**После успешной выкатки модалка предлагает перезагрузиться** (`lib/swUpdate.ts`). Это не
украшение: страница пережила рестарт продукта на кеше service worker, то есть выкативший
остался ровно на том бандле, который только что заменил, а штатная плашка обновления придёт
по таймеру — до минуты спустя. Кнопка делает то же, что плашка (`update()` → дождаться
`installed` → `SKIP_WAITING` → перезагрузка по `controllerchange`), но по требованию и без
второго `useRegisterSW`. При `rolled-back` кнопки нет: на бою осталась прежняя сборка.
Глобально переходить на `registerType: 'autoUpdate'` при этом НЕ надо — у продукта длинные
сессии, и самовольная перезагрузка посреди хода или набранного сообщения дороже ожидания.

Ограничение: событие живёт в сессионном пространстве имён, поэтому связь работает, пока
продукт и трей — в одной интерактивной сессии Windows (нынешняя топология: трей запускает
продукт). Другая даст безопасный отказ «трей не отвечает».

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

## Observability (OpenTelemetry)

Двухрежимная через OTel SDK: **dev** → Aspire Dashboard (in-memory), **production** → SigNoz
(ClickHouse, 30d traces / 90d metrics). Включение per-instance — секция `Telemetry` в
`appsettings.Local.json`; все порты (SigNoz UI :3301, OTLP :4317/4318) bind'ятся к `127.0.0.1`.
PII-санитайзер (`PiiSanitizingProcessor`) сидит первым в pipeline — оба backend'а получают
очищенные данные. **`SpendStore` = source of truth для billing (токены/стоимость), OTel-метрики
его НЕ дублируют.**

**Алерты** доставляются в уведомления CCS (категория «Алерт»): `AlertPollingService` раз в 60с
опрашивает `GET /api/v1/alerts` SigNoz. Опрос, а не webhook — боевой хост слушает HTTPS с сертом
на домен, и запрос из контейнера падает по SNI. Правила — код
(`docker/observability/alerts/*.json`), рассылает только инстанс с `Telemetry:Alerts:Enabled`.

**Раздел «Телеметрия» в UI** (admin-only): две вкладки — «Инциденты» (дефолт) и «SigNoz»
(встроен `<iframe>` через same-origin проброс `/telemetry-proxy/**`, включение —
`Telemetry:Ui:Enabled`).

**Инциденты** — разбор алерта из интерфейса: досье собирает ДЕТЕРМИНИРОВАННЫЙ код
(`Telemetry/Incidents`, запросы к `/api/v5/query_range`), **модель участвует только по кнопке
«Объяснить»** (место `incident-explain`). Инварианты: связка «инцидент → чат» держится на теге
`chat_id` и его строке в KEEP `PiiRules` (default-deny выбросит тег молча — сторож
`PiiSanitizerTests.ChatId_IsKept`); опции инцидентов регистрируются независимо от
`AlertsOptions.IsUsable`; погасшие алерты не забываются, а помечаются (`AlertStateStore`,
потолок 50), при этом `KnownFingerprints` отдаёт только горящие; алерт чужого контура даёт
плашку, а не пустой список. Форма запросов, состав досье (он же промпт «Объяснить») и
ограничения — [docs/observability/incident-queries.md](docs/observability/incident-queries.md).

Доки: [overview.md](docs/observability/overview.md) (архитектура, privacy, cardinality, sampling,
future epics) · [audit.md](docs/observability/audit.md) (карта существующих поверхностей) ·
[signoz-setup.md](docs/observability/signoz-setup.md) (развёртывание, retention, backup).
**Перед правками в `Telemetry/` или новыми метриками — прочитай
[docs/observability/overview.md](docs/observability/overview.md).**

## Реализовано

Ядро: auth по API-ключу, проекты, сессии, чат (вложения/голос/режимы ⚡📋❓), файловый
менеджер с diff/revert, empty states.

Поверх ядра: виджеты в чате (sandbox-iframe + строгая CSP), артефакты сессии, продуктовая
история «Что нового» (AI-сводка коммитов по дням), плагин oh-my-claudecode, задачи v3
(напоминания, регулярные, web push, Claude/персона-исполнитель), бэкапы каталога `data`
(расписание + `exe --backup/--restore/--inspect`, меню трея, виджет на главной), панель
«Документация» (корпус md с деревом, оглавлением, поиском, обратными ссылками и типами
документов со свойствами), панель «Сервисы» (дев-серверы проекта в iframe через прокси
`/preview/**` — **ключи и эндпоинты остались `preview`**, переименована только подпись),
ридер ссылок (панель «Чтение»: `POST /api/reader/read` и `GET /api/reader/image` под общим
SsrfGuard — [ADR-005](docs/adr/ADR-005-link-reader-server.md)).

**Голосовой режим чата** (`Session.VoiceMode`, одна кнопка `AudioLines` в композере — на месте
«Отправить» при пустом поле): ответ приходит коротким и читается вслух. Формат держат ДВЕ точки
промпта — секция `voice-mode` и оговорка последним блоком слоя персоны (без неё слот «Формат
ответов» персоны перебивает правило, слой клеится после секций). Озвучка — `POST /api/tts`
(Yandex SpeechKit REST v1, конфиг `Yandex:SpeechKit`, роль сервисного аккаунта
`ai.speechkit-tts.user`); коды 503 `not_configured` / 502 `upstream` — контракт фолбэка на голос
браузера, менять осознанно. Та же кнопка запускает **режим разговора** (hands-free): петля
«сказал → пауза 1–2.8 с по хвосту фразы (`pendingDelayFor`) → отправка → ход → ответ вслух →
снова слушаю», автомат чистым редьюсером
в `hooks/useHandsFree.ts`. Инвариант петли — микрофон и озвучка не пересекаются никогда (эхо):
микрофон открыт только в фазах слушания, фаза озвучки ведётся в `ChatPanel` с токеном вызова,
`speak()` резолвится по реальному концу звука на ОБОИХ путях (сервер и голос браузера).
Во время хода в углу композера только «Стоп» (прерывает ход, оставаясь в разговоре).
Разговору можно назначить **локального исполнителя** (место `chat-voice` → «Локальная»):
ходы идут прямым HTTP-вызовом Ollama мимо claude CLI **потоком** (`stream: true`, куски по
границе предложения — озвучка стартует до конца ответа), фронт не меняется.
Подробности — раздел «Голосовой режим чата» в [features.md](docs/architecture/features.md).

Детали каждой фичи — [docs/architecture/features.md](docs/architecture/features.md).

## Фич-флаги (feature toggles)

Dark launch: фича коммитится выключенной и включается per-user в меню «Экспериментальные
функции». Реестр (source of truth) — в коде: `FeatureFlagCatalog.All`
([Models/FeatureFlag.cs](backend/ClaudeHomeServer/Models/FeatureFlag.cs)); хранение —
override в `data/users.json`; фронт — стор [lib/featureFlags.ts](frontend/src/lib/featureFlags.ts),
хук `useFeature(FLAGS.key)`. Большинство старых флажных фич включены безусловно
(2026-08); в каталоге **два флага**: `workspace-destructive` (постоянный предохранитель от
необратимого удаления) и `change-dossiers-recall` (история решений по коду — подсказки
персонам и выгрузка отдельной веткой, [ADR-004](docs/adr/ADR-004-change-dossiers.md)).
Пометки «за флагом …» в доках — исторические; актуальный состав — в коде каталога.

Работают безусловно, без тумблера (флаги сняты 2026-08-21): **ассистент по умолчанию и
знакомство** — заготовка персоны заводится при первом входе, знакомство приходит
приглашением, а не обязательным экраном; проектное знакомство v2 раскладывает каркас папок
и правил по подтверждению карточкой в ленте
([docs/architecture/onboarding-intro.md](docs/architecture/onboarding-intro.md),
[docs/features/project-onboarding-v2.md](docs/features/project-onboarding-v2.md));
**фон проекта** — рисунок и цвет подбираются моделью по смыслу проекта, контракт генерации
без разметки и форма хранения — [ADR-008](docs/adr/ADR-008-project-background-generation.md),
тексты интерфейса — [docs/features/project-backgrounds.md](docs/features/project-backgrounds.md);
**карточка доклада о завершённой задаче** в чате постановщика вместе с новым промптом
реакции — [docs/features/task-completion-report.md](docs/features/task-completion-report.md).

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

Полная версия с разбором граблей — [docs/architecture/conventions.md](docs/architecture/conventions.md).
Главное:

- **ВАЖНО: CI гоняет тесты на Linux** (`ubuntu-latest`), а разработка идёт на Windows — тесты
  обязаны быть платформонезависимыми. Две ловушки: **пути** (строить от `Path.GetTempPath()` +
  `Path.Combine`, Windows-литералы на Linux считаются относительными) и **тайминги** (раннер
  слабее, ThreadPool голодает — ждать **событие** через `TaskCompletionSource` +
  `Task.WhenAny`, а не `Task.Delay`).
- **Категории тестов.** Большинство — юниты 1–50ms. Медленные: Controllers
  (`WebApplicationFactory`), `GitServiceTests` (`[Trait("Category", "Slow")]`), интеграционные MCP.
  Отдельно `[Trait("Category", "Dns")]` — тестам нужен настоящий резолв внешних имён, и на машине
  с Proxifier они валятся пачкой (среда, не регрессия): локально гоняй
  `dotnet test --filter "Category!=Dns"`. Фоновый автосейв `SessionManager` (внутри него sweep)
  в тестах выключен ключом `Session:AutoSaveSeconds = 0` — иначе фон меняет статусы между
  ассертами. На итеративную правку — `dotnet test --filter "FullyQualifiedName~<Набор>"`;
  полный прогон — перед коммитом/PR.
- **Одна папка — один проект на владельца** (`ProjectManager.EnsureRootFree`, 400 при повторе):
  датасет Dify ключуется по `RootPath`. У разных владельцев общая папка допустима.
- **Удаление чата уносит и транскрипт claude CLI** во всех профилях. Инвариант: только файл с
  точным именем `{csid}.jsonl`, никогда по маске и никогда сама папка (один `~/.claude` делят
  все инстансы плюс интерактивные сессии пользователя). `resumeSessionId` валидируется белым
  списком `^[A-Za-z0-9_-]{1,128}$` — иначе `".."` снёс бы всю папку `data`.
- **Настройки чата не двигают `UpdatedAt`** (по нему идут сортировка, секции дерева и
  непрочитанность); срок временного чата считается от `Session.ExpiryAnchor`.
- **Новое хранилище → сверься с бэкапом.** Всё в `data/` попадает в архив по умолчанию:
  кеш/логи — в исключения, секреты — в `BackupPaths.SecretFileNames`, сторы вне `data/` — в
  `BackupCore.CopyDataTo`, не-JSON стор — свой способ снимка, критичный стор — в
  `BackupValidation.Validate`. Ломающее изменение формата = инкремент `BackupSchema.Version`.
- **HTTP-клиент к опциональной зависимости — через `AddQuietHttpClient`**
  ([QuietHttpLogger.cs](backend/ClaudeHomeServer/Services/Http/QuietHttpLogger.cs)): дефолтный
  логгер печатает каждый провал как Error со стектрейсом и забивает консоль.
- Path traversal защита: `FileService.SafeJoin` — все пути через неё.
- Хранилище проектов — `data/projects.json`; метаданные сессий — `data/sessions.json`, история
  чата — `data/sessions/{claudeSessionId}/history.json`, resume через `--resume`.
- Комментарии в коде по-русски.

## Коммиты

- **Conventional Commits**: `type(scope): описание` (feat/fix/perf/docs/refactor/build/chore/ci/test/style).
- **Язык сообщений — русский** (в отличие от общего дефолта на английском).
- Трейлер `Co-Authored-By: <модель> <noreply@<домен-вендора>>` — где `<модель>` это
  та, что реально делала коммит (напр. «Claude Opus 4.8», «GLM 5.2»), а не фиксированная
  версия. Домен noreply берётся по вендору модели: Anthropic → `noreply@anthropic.com`,
  ZhipuAI (GLM) → `noreply@z.ai`. Без «Claude» в начале, если модель не от Anthropic.
- Атомарность: одно логическое изменение — один коммит.
- `commit`/`push` — только по явной просьбе.
