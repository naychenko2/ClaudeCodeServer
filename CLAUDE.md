# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Команды

> **Стандарт: сборка и тестирование — в dev-контейнере.** По умолчанию собираем и
> прогоняем приложение в контейнере (песочница для Claude + единое воспроизводимое
> окружение), а не на хосте. Подробности — [docs/docker.md](docs/docker.md).

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

## Архитектура

```
Browser (React 18 + TypeScript)
    │ SignalR WebSocket
    ▼
ASP.NET Core 9 (:5000)
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

Цвета из `frontend/src/lib/design.ts`:
- `accent: #D97757` — ОСНОВНОЙ цвет кнопок и активных состояний
- `bgMain: #F4F0E8` — фон страниц
- `bgPanel: #EDE7DA` — боковые панели
- `border: #E0D7C8` — границы

Шрифты: PT Serif (заголовки), Hanken Grotesk (UI), JetBrains Mono (код)
Стили: только inline-objects, без Tailwind/CSS-modules

## LLM-провайдеры (Services/Llm)

Единственный рантайм — claude CLI (`Llm/Claude/ClaudeSession`). Сторонние провайдеры
с Anthropic-совместимым эндпоинтом (DeepSeek, GLM) подключаются env-оверрайдами
процесса на каждый ход: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`,
`ANTHROPIC_MODEL`/`ANTHROPIC_DEFAULT_OPUS|SONNET_MODEL` (= модель сессии),
`ANTHROPIC_DEFAULT_HAIKU_MODEL`/`CLAUDE_CODE_SUBAGENT_MODEL` (= `SmallModel`),
плюс `ExtraEnv` провайдера (у GLM — `API_TIMEOUT_MS`). Весь функционал CLI (скиллы,
субагенты, workflow, план, compact, MCP, permissions, resume) работает одинаково
у всех провайдеров.

- **Конфиг** — секция `LlmProviders` (словарь key → провайдер): `DisplayName`,
  `AnthropicBaseUrl` (для CLI), `ApiBaseUrl` (нативный API — баланс, GET /models),
  `ApiKey` (в appsettings.Local.json; пустой = провайдер выключен и модели скрыты),
  `SmallModel`, `Balance` (вид источника баланса: `deepseek` = GET /user/balance;
  пусто — без баланса, как у GLM), `QueryModelsApi`, `SupportsImages`, `Models`
  (Id/DisplayName/ContextWindow/цены $ за 1M — по ним считается стоимость хода).
- **`LlmProviderRegistry`** — резолв провайдера из `Session.Model` (по каталогу
  моделей, затем по префиксу ключа; провайдер не персистится), `CapabilitiesFor`,
  `BuildCliEnv`, `ComputeCost` (на стороннем эндпоинте total_cost_usd от CLI
  считается по ценам Anthropic — пересчитываем по ценам конфига; без цен — null).
- **Guard**: смена провайдера у начатой сессии — 400 (транскрипт живёт у эндпоинта).
- **Профили CLI** — `data/claude-profiles/{key}` (CLAUDE_CONFIG_DIR): изоляция от
  OAuth-логина ~/.claude (иначе CLI шлёт провайдеру токен подписки → 401); туда же
  докладываются общие настройки пользователя по белому списку (CLAUDE.md,
  settings.json, rules/skills/agents/commands; креденшалы — никогда), источник —
  `ClaudeUserProfileDir` (дефолт ~/.claude), троттлинг 5 мин.
- **Баланс** — `ProviderBalanceService`, `GET /api/providers/{key}/balance|usage`
  (кэш 5 мин; снапшоты 8 дней в data/provider-usage-{key}.json, legacy
  deepseek-usage.json читается) — попап контекст-бейджа шапки чата + вкладка
  провайдера на экране «Использование».
- **Каталог моделей** — `ModelCatalogService`: записи `Models` конфига + при
  `QueryModelsApi` опрос `GET {ApiBaseUrl}/models` (новые модели с дефолтами).

Возможности провайдера (`LlmCapabilities`: displayName/plan/compact/mcp/effort/images/…)
отдаются фронту в блоке `providers` из `GET /api/models` и в `session_started`;
у CLI-провайдеров всё как у Claude, кроме `SupportsImages` (из конфига; DeepSeek — false).
UI скрывает недоступное (`useModelCaps` в `lib/models.ts`), брендинг (assistantName,
плашка стоимости/баланса, группы ModelPicker) — по `displayName`. Общие хелперы:
`TurnFileWatcher` (file_changed на время хода), `AttachmentInliner` (инлайн вложений),
`TasksServerLocator`. Модель Claude-исполнителя задач — `Tasks:ExecutorModel`; AI-генерация
описания/подзадач — `Tasks:AiModel`; сводки «Что нового» — `Changelog:Model` (везде
валидна модель любого провайдера: one-shot идёт через claude --print с теми же env).

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

## MCP-сервер задач (mcp/tasks-server)

Один файл [mcp/tasks-server/index.js](mcp/tasks-server/index.js) — чистый Node (stdio JSON-RPC,
**без зависимостей**, npm install не нужен). Инструменты: `tasks_list`, `tasks_search`,
`tasks_get`, `tasks_create`, `tasks_update`, `tasks_complete`, `tasks_delete`,
`tasks_add_subtask`, `tasks_toggle_subtask`.

Подключение автоматическое (за фич-флагом `tasks` владельца): `ClaudeSession.BuildTurnMcpConfig`
каждый ход собирает временный MCP-конфиг (серверы из `McpConfigPath` + `tasks`) и передаёт env:
`TASKS_API_URL` (адрес Kestrel или конфиг `McpTasksApiUrl`), `TASKS_API_TOKEN`
(сервисный JWT владельца сессии, `JwtService.IssueServiceToken`), `TASKS_PROJECT_ID`
(пусто = чат вне проекта → контекст личных задач). В системный промпт добавляется
подсказка об инструментах. Задачи per-owner: токен владельца ограничивает доступ его задачами.

## Заметки (Obsidian-совместимая база знаний)

Раздел «Заметки» (4-й хаб-таб, фич-флаг `notes`): markdown-vault со связями
`[[wikilinks]]`, backlinks, unlinked mentions и графом. Заметки — настоящие `.md` файлы:
личный vault `data/notes/{userId}` + `notes/` проектов владельца (в дереве файлов папка
всегда видна первой как «Заметки»). Единый per-owner граф; изоляция — сервисный JWT
(как задачи).

- **Бэкенд**: [NotesService.cs](backend/ClaudeHomeServer/Services/NotesService.cs) —
  скан источников (кэш TTL 2с), парсер frontmatter/`[[links]]`/inline-`#тегов`, резолв
  с коллизиями (`[[Проект/Имя]]`), backlinks, unlinked mentions, авто-обновление входящих
  ссылок при переименовании, фрагменты `#заголовок`/`^блок` (ExtractFragment), шаблоны
  `templates/` ({{title}}/{{date}}/{{time}}), daily notes `Journal/YYYY-MM-DD.md`.
  [NotesController.cs](backend/ClaudeHomeServer/Controllers/NotesController.cs) —
  `/api/notes/*` (CRUD, resolve, attachment, graph, sources, caps, templates, daily,
  link-mention, semantic, reindex, suggest-links/tags, daily/summary); realtime —
  `NotesChangedMessage` в группу user_*.
- **Семантика (Dify RAG)**: [NotesKnowledgeService.cs](backend/ClaudeHomeServer/Services/NotesKnowledgeService.cs) —
  dataset per-owner «{username}:notes», дифф-синхронизация по хешам (дебаунс 15с на
  мутации), store `data/notes-knowledge.json`; `KnowledgeService.RetrieveAsync` —
  Dify retrieve. Без `Dify:ApiKey` — тихо выключено (`caps.semantic=false`).
- **ИИ-фичи**: [NotesAiService.cs](backend/ClaudeHomeServer/Services/NotesAiService.cs)
  (модель `Notes:AiModel`, дефолт haiku) — предложение связей, авто-теги, конспект дня;
  one-shot вызовы через общий [OneShotClaudeRunner.cs](backend/ClaudeHomeServer/Services/Llm/OneShotClaudeRunner.cs)
  (на нём же TaskAiService).
- **MCP**: [mcp/notes-server/index.js](mcp/notes-server/index.js) (без зависимостей) —
  notes_list/search/read/create/update/backlinks/graph/delete/semantic_search; подключение
  как tasks-server (env NOTES_API_URL/TOKEN/PROJECT_ID в BuildTurnMcpConfig + подсказка
  в системный промпт).
- **Фронт**: [features/notes/](frontend/src/features/notes/) — NotesPage (список по
  источникам, поиск с операторами `tag:`/`source:` и режимом «По смыслу», граф с
  drag-pin/фильтрами), NoteView (просмотр/правка, backlinks, упоминания с «Связать»,
  локальный граф, ✨-кнопки), NoteEditor — CodeMirror 6 c live preview (скрытие маркеров,
  интерактивные чекбоксы, Ctrl+клик по ссылке) и автокомплитом `[[`/`#`;
  [MarkdownViewer.tsx](frontend/src/components/MarkdownViewer.tsx) — рендер wikilinks
  (живая/призрачная/внешняя), embeds `![[…]]`, hover-preview; стор
  [lib/notes.ts](frontend/src/lib/notes.ts) (realtime notes_changed).

## Персоны

Концепция **«Персоны = контакты, Чаты = разговоры»** (фич-флаг `personas`): глобальный раздел
«Персоны» (хаб-таб) и вкладка «Команда» внутри проекта — **только настройка** (профиль + память);
разговор с агентом живёт среди обычных чатов и везде помечен его лицом (аватар/роль/цвет).
Персона — **отдельная сущность** (JSON-стор, не .md-агент): роль (главная в отображении:
«Роль (Имя)»), имя, характер, аватар, модель/усилие, зона, приветствие, долгая память.
Изоляция per-owner (как задачи/заметки).

- **Модель**: [Persona.cs](backend/ClaudeHomeServer/Models/Persona.cs) — `Persona`
  (Name, Role, Handle, Description, SystemPrompt, Model/Effort, Scope `Global|Project`, ProjectId,
  Avatar `{Kind initials|image, Color, ImageFile}`, Greeting, MemoryEnabled) +
  `PersonaMemoryEntry` (Type `Semantic|Episodic|Procedural`, Text, Tags, Salience). Хранилище —
  `data/personas.json`, ассеты (аватары) — `data/personas/{id}/`.
- **CRUD**: [PersonaManager.cs](backend/ClaudeHomeServer/Services/PersonaManager.cs) — per-owner
  (`Get(id, userId)` проверяет OwnerId), генерация уникального slug-`Handle`.
  [PersonasController.cs](backend/ClaudeHomeServer/Controllers/PersonasController.cs) —
  `/api/personas/*` (CRUD с фильтрами `?scope=context|project|global&projectId=`, `{id}/chats`,
  `{id}/memory*`, `{id}/avatar*`, `ai/character` — LLM-генерация/улучшение характера с
  уточняющим промптом); realtime — `PersonasChangedMessage` (created/updated/deleted/memory).
- **Чат с персоной**: `Session.PersonaId`; `SessionManager.CreatePersonaChatAsync` маршрутизирует
  по зоне — **глобальная** персона → чат вне проекта (scope = все данные владельца, `ProjectId=null`
  в tasks/notes MCP), **проектная** → сессия в её проекте (scope = только проект). Характер персоны
  инжектится в системный промпт как персональный слой ([ClaudeSession.cs](backend/ClaudeHomeServer/Services/Llm/Claude/ClaudeSession.cs),
  приоритет над .md-агентом); персона-слой (промпт+память) восстанавливается и после рестарта
  (`BuildPersonaLayer` в `EnsureProcessAsync`). Назначение/смена собеседника —
  `SessionManager.SetPersona` (`POST /chats/{id}/persona`, `POST .../sessions/{sid}/persona`),
  разрешена и ПО ХОДУ разговора: слой пересобирается каждый ход, транскрипт продолжается
  через `--resume`; модель персоны применяется только при том же провайдере;
  `Session.PersonaSwitched` добавляет в промпт оговорку про чужие прошлые ответы, на фронте
  локальный разделитель «Теперь отвечает: …». Scope НЕ требует спец-логики — он
  предопределён типом сессии.
- **Шаблоны ролей** ([personaTemplates.ts](frontend/src/features/personas/personaTemplates.ts)):
  6 готовых ролей с промптами-контрактами (Ревьюер, Планировщик, Аналитик, Ментор, Секретарь,
  Дизайнер) — сетка карточек на экране создания (PersonaQuickCreate), выбор предзаполняет
  PersonaForm (`initial`), включая дефолтные возможности.
- **Возможности per-persona** (`Persona.Tools`: ключи `tasks`/`notes`/`web`; null = без
  ограничений, полный набор нормализуется в null): гейт tasks/notes MCP при сборке
  LlmSessionContext; выключенный `web` добавляет WebSearch/WebFetch в
  `ExtraDisallowedTools` (поверх `Claude:DisallowedTools`). UI — секция «Возможности»
  (3 тумблера) в PersonaForm.
- **@упоминания (флаг `persona-mentions`)**: надстройка над MCP персон — при включённом
  флаге и наличии других персон в контексте personas-server получает env `PERSONAS_MENTIONS=1`
  (регистрирует инструмент `persona_ask`) и `PERSONAS_SELF_ID`, а в промпт добавляется
  подсказка со списком «@handle — Роль (Имя)» (`BuildPersonasContext.MentionsHint`).
  `POST /api/personas/ask` — one-shot
  ответ персоны от её лица (слой `BuildPersonaPrompt` + recall памяти + модель персоны,
  `OneShotClaudeRunner`; таймаут `Persona:AskTimeoutMs`, дефолт 120с). Анти-рекурсия по
  построению: one-shot без MCP, глубина делегирования 1. Фронт: автокомплит `@` в Composer
  ([MentionsDropdown.tsx](frontend/src/components/MentionsDropdown.tsx)). Handle персон
  транслитерируется из кириллицы (PersonaManager.Slugify).
- **Дискуссия «Обсудить с командой»** (поверх @упоминаний, тот же флаг): кнопка в композере
  чата персоны → [DiscussTeamDialog.tsx](frontend/src/features/personas/DiscussTeamDialog.tsx)
  (выбор 1-2 участников + вопрос) → обычное сообщение с промпт-обвязкой: ведущая персона
  опрашивает участников через persona_ask, возражает при разногласиях (один раунд) и сводит
  итог. Бэкенд и протокол не участвуют. При включённом `persona-group-chats` в диалоге есть
  второй режим — «Совещание» (см. ниже).
- **Контракт характера (P1) + дисциплина (P2)**: `Persona.Contract` (character/tone/mustDo/
  mustNot/outputFormat/speechExamples — слоты вместо единого текста; legacy `SystemPrompt`
  остаётся у персон без контракта). Единый сборщик промпта —
  [PersonaPromptBuilder.cs](backend/ClaudeHomeServer/Services/PersonaPromptBuilder.cs):
  идентичность + секции контракта + дисциплинарная обвязка по провайдеру модели
  (Claude — только краткость; DeepSeek — полный набор; GLM — без секции достоверности).
- **Память v2 (P3)**: скоринг взвешенной суммой ([PersonaMemoryScorer.cs](backend/ClaudeHomeServer/Services/PersonaMemoryScorer.cs)),
  reinforcement (Touch при recall) и **рабочий фокус** «что я сейчас делаю» (одна ячейка,
  в recall первым блоком; `GET/DELETE {id}/focus`). Autolearn выставляет salience и фокус;
  фоновая консолидация (LLM-merge дублей + вытеснение) — за флагом `persona-memory-consolidation`
  ([PersonaMemoryConsolidationService.cs](backend/ClaudeHomeServer/Services/PersonaMemoryConsolidationService.cs)).
- **Профили доступа (P6)**: `Persona.Access` — `full` | `readOnly` (смотрит и советует, ничего
  не меняет) | `custom` (свой список `DisallowedTools`); в disallowed-инструменты сессии их
  превращает [PersonaAccessPolicy.cs](backend/ClaudeHomeServer/Services/PersonaAccessPolicy.cs)
  (`BuildExtraDisallowed`, поверх ограничений `Tools`).
- **Проактивность (флаг `persona-proactive`)**: `Persona.Proactive` (daily/weekdays/weekly +
  время в таймзоне владельца + инструкция) — [PersonaProactiveService.cs](backend/ClaudeHomeServer/Services/PersonaProactiveService.cs)
  (BackgroundService) в момент расписания пишет первой в закреплённый чат персоны
  (`Proactive.SessionId`, проверка `chat.PersonaId == personaId`) и шлёт уведомление.
- **Групповой чат (флаг `persona-group-chats`)**: `Session.Participants` (2-4 id персон,
  первая — ведущая), `Session.PersonaId` = активный спикер. Создание —
  `SessionManager.CreateGroupChatAsync` (`POST /api/chats/group`; зона — по ведущей, как у
  CreatePersonaChatAsync), состав — `PUT /api/chats/{id}/participants` (спикер сохраняется,
  если остался, иначе ведущая). Роутинг хода — [GroupChatRouter.cs](backend/ClaudeHomeServer/Services/GroupChatRouter.cs)
  (первый @handle участника в тексте → спикер, остальные → AlsoMentioned; без упоминаний —
  текущий/ведущая) в `SendMessageAsync` до пересоздания процесса: `SwitchSpeaker` (общее ядро
  с SetPersona) + `speaker_changed` клиентам (разделитель «Теперь отвечает: …», рендер общий
  с companion_switched; в истории derive по смене personaId — `normalizeHistory`).
  Промпт спикера получает групповую надстройку (`BuildGroupChatHint`: участники + «говори
  только за себя, остальных спрашивай persona_ask»), mentions-режим MCP персон в группе
  включён всегда (независимо от `persona-mentions`), MentionsHint — по участникам.
  UI: мультивыбор «Групповой чат…» в CompanionSelector (чекбоксы 2-4, метка «ведущая»,
  предупреждение про разных провайдеров), стек аватаров в ChatHeaderBar (активный — с
  цветным кольцом), участники первыми в @автокомплите.
- **Совещание (P7, тот же флаг)**: [PersonaMeetingService.cs](backend/ClaudeHomeServer/Services/PersonaMeetingService.cs) —
  `POST /api/chats/{id}/meeting` {question, personaIds?} (дефолт — participants чата; в чате
  одной персоны personaIds обязателен), `POST .../meeting/cancel`. Три фазы one-shot через
  [PersonaAskService.cs](backend/ClaudeHomeServer/Services/PersonaAskService.cs) (вынос
  persona_ask из контроллера; интерфейс `IOneShotRunner` для моков): independent (N параллельных
  позиций, ≤3 одновременно) → attack (каждому — позиции остальных: «критикуй слабое, защищай
  своё, явно уступай сильному аргументу») → synthesis (итог от ведущей её моделью). Итого
  ~2N+1 вызовов; упавшая персона → IsError-entry, <2 живых — фаза error; общий колпак
  `Persona:MeetingTimeoutMs` (600с), персональный — `Persona:AskTimeoutMs`. Каждая фаза
  пишется в историю вне хода (`StoredMeetingPhaseMessage` через `SessionManager.AppendStoredAsync` —
  обобщённый паттерн PublishFalCostAsync) + live `meeting_phase`/`meeting_progress`.
  Фронт: карточка [MeetingView.tsx](frontend/src/components/chat/MeetingView.tsx) (аккордеон
  по персонам, спиннеры прогресса, «Итог» крупно, «Продолжить обсуждение» — краткий итог
  обычным сообщением в транскрипт CLI, «Отменить»); запуск — переключатель
  «Обсуждение/Совещание» в DiscussTeamDialog (лимит 4, подпись «≈2N+1 вызовов»).
- **Долгая память** (типизация 2026 semantic/episodic/procedural):
  [PersonaMemoryService.cs](backend/ClaudeHomeServer/Services/PersonaMemoryService.cs) — записи в
  `data/persona-memory.json` (источник правды) + семантический слой в Dify-датасет
  `{username}:persona:{handle}` (дифф по хешам, дебаунс 15с; без Dify — полнотекст-fallback).
  Retrieval со скорингом `relevance × recency(полураспад 30д) × typeWeight(0.6/0.3/0.1) × salience`.
  Auto-recall в системный промпт каждого хода (`BuildPersonaRecallProvider`, независим от заметок).
- **MCP**: [mcp/memory-server/index.js](mcp/memory-server/index.js) (без зависимостей) —
  memory_remember/search/list/forget; подключение как tasks/notes (env `MEMORY_API_URL/TOKEN/PERSONA_ID`
  в `BuildTurnMcpConfig` + подсказка в промпт). Явный write-path: персона сама решает, что запомнить.
- **MCP персон**: [mcp/personas-server/index.js](mcp/personas-server/index.js) (без зависимостей) —
  personas_list/get/create/update/delete/generate_avatar (CRUD персон из любого чата; создание
  глобальных и проектных — дефолтный projectId из сессии). Подключение как tasks/notes
  (env `PERSONAS_API_URL/TOKEN/PROJECT_ID` в `BuildTurnMcpConfig` + подсказка в промпт), но только
  при включённом у владельца флаге `personas` (`SessionManager.BuildPersonasContext`).
  generate_avatar = avatar/generate `{count:1}` + select первого кандидата.
- **Аватар**: инициалы+цвет (палитра `AGENT_COLORS`) базой; фото-генерация через fal.ai —
  [FalImageService.cs](backend/ClaudeHomeServer/Services/FalImageService.cs) (`Fal:ApiKey`, модель
  `Fal:ImageModel`, дефолт `fal-ai/flux/schnell`; для фото-аватаров задают `flux/dev`). Генерация
  возвращает 1-4 **кандидата** (`POST {id}/avatar/generate` {prompt?,count?} → candidates во временную
  папку, аватар НЕ меняется), пользователь выбирает (`POST {id}/avatar/select`), отдача — `GET {id}/avatar`
  (access_token в query для `<img>`). Плюс **загрузка своего фото** с кропом и зумом
  (`POST {id}/avatar/upload` — original + cropped + параметры кропа; валидация по magic bytes)
  и перекроп сохранённого оригинала без перезагрузки файла (`POST {id}/avatar/recrop`,
  `GET {id}/avatar/original`).
- **Авто-память** (флаг `persona-memory-autolearn`): [PersonaMemoryAutolearnService.cs](backend/ClaudeHomeServer/Services/PersonaMemoryAutolearnService.cs) —
  IHostedService на `SessionManager.OnSessionMessage`; по завершении хода персонной сессии one-shot
  извлекает факты (semantic) и итог (episodic) из транскрипта и сохраняет в память (дедуп в `Remember`).
- **Фронт**: [features/personas/](frontend/src/features/personas/) — PersonasPage (глобальный раздел,
  только `scope=global`): сайдбар PersonaList | центр «Студия-профиль»; редактор
  [PersonaForm.tsx](frontend/src/features/personas/PersonaForm.tsx) — одна колонка 680 в стиле
  TaskEditForm: hero-аватар 80 (инлайн-генерация 4 кандидатов + цвет), безрамочная serif-«Роль»,
  Характер во всю ширину (липкая панель пресетов + ✨Сгенерировать/✨Улучшить с уточняющим
  промптом-поповером, autoGrow без скролла), Поведение (модель/усилие/зона/приветствие),
  Память-summary (счётчики + «Открыть память»); действия — в [PersonaToolbar.tsx](frontend/src/features/personas/PersonaToolbar.tsx)
  (общий Toolbar: Профиль|Память, Поговорить, ⋯-меню с Удалить, Сохранить + dirty-индикатор).
  В проекте — вкладка «Команда» (`leftTab='agents'` WorkspacePage): список в сайдбаре
  ([ProjectPersonasPanel.tsx](frontend/src/features/personas/ProjectPersonasPanel.tsx)), форма — в
  контентной зоне. Идентификация в чатах: плашки ChatList/SessionList (аватар+«Роль (Имя)»+цвет),
  агент в тулбаре чата (ChatHeaderBar: аватар+роль/имя+зона+полоса цвета), аватар у реплик
  (PersonaContext→ChatItemView), приветствие (PersonaGreeting). Запуск чата: «Поговорить» из
  студии, [PersonaSelector](frontend/src/components/PersonaSelector.tsx) в композере пустого чата
  (группы «Команда проекта»/«Глобальные»), пилюли «Поговорить с…» в empty state (в проекте
  команда сразу, глобальные за «+N ещё»). Стор [lib/personas.ts](frontend/src/lib/personas.ts)
  (realtime personas_changed; `personaLabel`/`personaTitleLines` — единый формат «Роль (Имя)»).
- **Флаги**: `personas` (раздел + чат + память + аватар), `persona-memory-autolearn` (авто-извлечение
  фактов из диалога), `persona-memory-consolidation` (фоновая уборка памяти), `persona-mentions`
  (@упоминания + persona_ask + дискуссия), `persona-proactive` (пишет первой по расписанию),
  `persona-group-chats` (групповые чаты + совещания P7).

## REST API

Все эндпоинты (кроме `/api/auth/ping`) и SignalR-хаб защищены `[Authorize]` —
доступ только по API-ключу. `ping` дополнительно под rate-limit (`Auth:PingRateLimit`,
по умолчанию 10/мин на IP). См. [docs/remote-access.md](docs/remote-access.md).

```
POST /api/auth/ping             { serverUrl, apiKey } → { ok } | 401 | 429  (ключ + rate-limit)
GET/POST/PUT/DELETE /api/projects
GET/POST/DELETE     /api/projects/{id}/sessions       POST body: { mode, name?, resumeSessionId?, model? }
PUT                 /api/projects/{id}/sessions/{sid} body: { name?, model? } → обновлённая сессия
GET                 /api/projects/{id}/files          ?path=
GET                 /api/projects/{id}/files/search   ?q=
GET/PUT             /api/projects/{id}/files/content  ?path=  → { content, isBinary, isImage, base64?, ... }
GET                 /api/projects/{id}/files/diff     ?path=  → { diff }
POST                /api/projects/{id}/files/revert   { path }
POST                /api/projects/{id}/files/create   { path }
POST                /api/projects/{id}/files/mkdir    { path }
POST                /api/projects/{id}/files/rename   { oldPath, newPath }
DELETE              /api/projects/{id}/files          ?path=
GET                 /api/history/days                 ?sinceDays= → [{ date, commitCount, cached }]  (по всем проектам, без LLM)
GET                 /api/history/day/{date}                       → { date, items[] }  (продуктовая AI-сводка дня, кеш)
GET                 /api/history/new-count            ?since=iso  → { count } (новые коммиты во всех проектах после даты; для бейджа)
GET                 /api/feature-flags                → { definitions[], values{} }  (реестр + эффективные значения юзера)
PUT                 /api/feature-flags/{key}          { enabled } → { values{} }      (override per-user; ключ валидируется по каталогу)
PUT                 /api/auth/timezone                { timeZone }  (IANA-зона устройства — для напоминаний)
POST                /api/tasks/{id}/execute           → Task  (запуск Claude-исполнителя, флаг task-claude-exec)
GET                 /api/push/vapid-public-key        → { publicKey }
POST                /api/push/subscribe|unsubscribe   { endpoint, p256dh?, auth? }  (web-push подписки устройств)
GET/POST/PUT/DELETE /api/personas                     (CRUD персон; ?scope=context&projectId= — доступные в контексте)  [флаг personas]
GET/POST            /api/personas/{id}/chats          POST body { mode?, resumeSessionId?, name? } → Session (чат от лица персоны)
GET/POST            /api/personas/{id}/memory         ?type=  / body { type, text, tags? } → записи памяти
GET                 /api/personas/{id}/memory/search  ?q=&topK=  → hits (relevance×recency×type)
DELETE              /api/personas/{id}/memory/{entryId}
POST                /api/personas/{id}/avatar/generate { prompt? } → Persona  (AI-аватар через fal)
GET                 /api/personas/{id}/avatar          → картинка (access_token в query для <img>)
POST                /api/personas/ask                  { handle, question, context? } → { handle, name, role, answer }
                                                       (one-shot ответ персоны от её лица; флаг persona-mentions; дёргается MCP personas-server)
POST                /api/chats/group                   { personaIds[], mode?, name? } → Session  (групповой чат, флаг persona-group-chats)
PUT                 /api/chats/{id}/participants       { personaIds[] } → Session  (состав группы; спикер сохраняется, иначе ведущая)
POST                /api/chats/{id}/meeting            { question, personaIds? } → { meetingId }  (совещание P7; дефолт — participants чата)
POST                /api/chats/{id}/meeting/cancel     → 204  (отмена идущего совещания)
```

Эффективные значения флагов также возвращаются в `GET /api/auth/me` (поле `featureFlags`),
чтобы фронт получал их тем же запросом, что и при старте. Подробнее — раздел «Фич-флаги».

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

## Реализовано

- Auth: реальная аутентификация по API-ключу — `[Authorize]` на всех API + хабе.
  Ключ из `Auth:ApiKey` (env/config) или автоген в `data/auth-key.txt` (печатается в консоль).
  Клиент: `Authorization: Bearer` (REST), `?access_token=` (WS); 401 → авто-логаут.
  Удалённый доступ (Tailscale + HTTPS): [docs/remote-access.md](docs/remote-access.md)
- Проекты: CRUD, редактирование, выход
- Сессии: создание с именем/режимом/моделью, редактирование названия и модели (шапка чата + список), статусы (starting/active/waiting/finished/error)
- Чат: Composer (вложения, режим ⚡/📋/❓, голосовой ввод, стоп, «Claude печатает…»)
- Сообщения: text, thinking, tool_use (spinner), permission_request, file_changed, result, error+retry
- Empty states: нет сессий, пустой чат с подсказками, пустая папка, нет результатов поиска
- Файловый менеджер: дерево, поиск, просмотр/редактирование, diff/revert, бинарные, изображения, loading
- Несохранённые изменения: диалог при закрытии файла
- Артефакты сессии (за фич-флагом `session-artifacts`): панель справа от чата с вкладками — план (ExitPlanMode), задачи (Todo/Task), агенты (субагенты Task/Agent + workflow-группы с раскрытием деталей: промпт, лента вызовов, результат), изменённые/упомянутые файлы, ссылки. Всё derived из ленты чата ([useSessionArtifacts.ts](frontend/src/hooks/useSessionArtifacts.ts)), без участия бэкенда
- Продуктовая история «Что нового» (основной функционал, кнопка в шапке): AI-сводка изменений **по всем проектам сразу** — что нового и чем полезно пользователю (не код, не diff). [ChangelogService](backend/ClaudeHomeServer/Services/ChangelogService.cs) собирает git-коммиты из репы продукта — путь в `Changelog:SourceRepoPath` (машинно-специфичный, в `appsettings.Local.json`; без него раздел показывает «не настроено»), имя — `Changelog:SourceProjectName` (дефолт = имя папки). Дальше группирует по дням и суммирует каждый день **одним вызовом** через общий [OneShotClaudeRunner](backend/ClaudeHomeServer/Services/Llm/OneShotClaudeRunner.cs) (модель `Changelog:Model`, дефолт haiku; лениво, продуктовый промпт — польза, а не техника; таймаут `Changelog:TimeoutMs`, дефолт 480с). Промпт просит не более 12 пунктов на день (агрессивная группировка) и короткий `scoreReason`. Области выравниваются между днями подсказкой частых `area` из кеша (`KnownAreas`) + канонизацией `NormalizeAreas` (схлопывает «Чат»/«чат»/«ЧАТ»). Fallback без LLM (`FallbackItems`, при недоступном claude) кладёт `area` по типу коммита (feature→«Новое», fix→«Исправления», improvement→«Улучшения», иначе «Прочее») и честный `scoreReason` «сводку собрать не вышло». **Дробить день на параллельные чанки пробовали — отказались**: старт CLI (~15с) платится за каждый вызов, а чанки не видят друг друга и дробят смысл (замер на 59 коммитах: один вызов — 141с/13 пунктов, три чанка — 182с/29 пунктов). Каждый пункт: `type` (feature/improvement/fix/other), `area` (раздел продукта — Claude определяет сам), `emoji`, `title`, `benefit`, `authors`, `projects`. Результат кешируется на уровне продукта в `data/changelog/product.json` (ключ дня = хеш sha-набора всех проектов — сводка одна для всех и перегенерируется только при новых коммитах дня). Алиасы авторов — `Changelog:AuthorAliases` (email → имя). Эндпоинты — глобальный [HistoryController](backend/ClaudeHomeServer/Controllers/HistoryController.cs) (`api/history/*`). Фронт: [ProductHistory.tsx](frontend/src/components/ProductHistory.tsx) — полноэкранная лента по дням (Сегодня/Вчера/дата). Внутри дня пункты **сгруппированы по области** (`area`), и режим показа адаптивный (`LIST_MODE_MAX = 12`): мало пунктов — все области идут **секциями списком** (заголовок `CategoryHeader` + свой таймлайн), много — **вкладки-подчёркивания** `AreaTabs` с таймлайном активной области. Таймлайн (`GroupTimeline`) — маркеры-кружочки единым accent-цветом. Иконки авторов — роли (`AUTHOR_EMOJI`: Григорий 🧑‍💼, Андрей 👨‍💻; новые — из пула детерминированно по имени). Фильтр по исполнителю (чипы, авторы по алфавиту; режим считается от отфильтрованных пунктов). Навигация по дням — **календарь** (`DayCalendar`): дни с изменениями кликабельны, сводка генерится лениво только для выбранного дня. Кнопка «Что нового» в [HubHeader](frontend/src/components/HubHeader.tsx) видна во всех разделах (событие `open-product-history` → overlay в [App.tsx](frontend/src/App.tsx)), бейдж считает новые коммиты с последнего захода (timestamp в `localStorage`)
- Фич-флаги: per-user тогглы (dark launch), реестр в коде (`FeatureFlagCatalog`), UI-тумблеры в меню аватара («Экспериментальные функции»). См. раздел «Фич-флаги»
- Задачи v3 (флаги `task-reminders`/`task-recurrence`/`task-claude-exec`):
  - Напоминания: `TaskItem.ReminderMinutes` (офсет от срока), `TaskSchedulerService` (BackgroundService, тик 30 с) шлёт `NotificationMessage` в группу user_* (тост [NotificationToasts.tsx](frontend/src/components/NotificationToasts.tsx)) + web push. Сроки локальные: `User.TimeZone` (IANA, фронт шлёт при старте), конверсия в UTC — [TaskDueCalculator.cs](backend/ClaudeHomeServer/Services/TaskDueCalculator.cs), без времени — 09:00
  - Web push: VAPID-ключи автогенерация в `data/vapid-keys.json`, подписки в `data/push-subscriptions.json` (несколько устройств per-user, авточистка 404/410). SW — свой `frontend/src/sw.ts` (vite-plugin-pwa `injectManifest`, отдельный tsconfig.sw.json), обработчики push/notificationclick с hash-диплинками
  - Регулярные задачи: `TaskRecurrence` + `SeriesId`; при переводе экземпляра в done PUT /api/tasks/{id} спавнит следующий ([TaskRecurrenceCalculator.cs](backend/ClaudeHomeServer/Services/TaskRecurrenceCalculator.cs) — отсчёт от срока, не от завершения)
  - Claude-исполнитель: [TaskExecutionService.cs](backend/ClaudeHomeServer/Services/TaskExecutionService.cs) — сессия acceptEdits в проекте задачи (личная — чат вне проекта), промпт с правилами ведения статуса через MCP tasks_*; наблюдение через событие `SessionManager.OnSessionMessage` (result → отметка + уведомление, permission → «ждёт ответа»); триггеры: кнопка «Выполнить с Claude» и автозапуск планировщиком в момент срока (окно 24 ч)

## Фич-флаги (feature toggles)

Позволяют коммитить фичу выключенной и включать её по флагу без пересборки (dark launch).
Флаги **per-user**: каждый юзер сам включает себе в меню «Экспериментальные функции».

- **Реестр (source of truth) — в коде:** `FeatureFlagCatalog.All` в
  [Models/FeatureFlag.cs](backend/ClaudeHomeServer/Models/FeatureFlag.cs). Каждый флаг:
  `Key`, `Title`, `Description`, `Default`, `Stage` (`dev`/`beta`/`stable` — метка зрелости в UI).
- **Хранение:** override per-user в `data/users.json` (поле `FeatureFlags`). Эффективное
  значение = override юзера ?? дефолт реестра ([FeatureFlagService](backend/ClaudeHomeServer/Services/FeatureFlagService.cs)).
- **Фронт:** стор на `useSyncExternalStore` ([lib/featureFlags.ts](frontend/src/lib/featureFlags.ts)),
  значения грузятся из `/api/auth/me` при старте. Проверка в UI — хук `useFeature(FLAGS.key)`.

**Как добавить новый флаг (3 шага):**
1. Бэк: добавить строку в `FeatureFlagCatalog.All` (`key`, `title`, `description`, `Default: false`, `stage`).
2. Фронт: добавить ключ в const `FLAGS` в `lib/featureFlags.ts`.
3. Обернуть фичу: `{ useFeature(FLAGS.myFeature) && <MyFeature /> }`.

Тумблер в модалке появится сам (рендерится из каталога). Ключи дублируются в двух местах
(C#-каталог и TS-`FLAGS`) — при переименовании править оба.

## Агенты (.claude/agents/)

| Агент | Роль |
|---|---|
| `project-manager` | PM, принимает решения вместо пользователя |
| `frontend-dev` | React/TypeScript (frontend/src/) |
| `backend-dev` | C#/.NET (backend/) |
| `architect` | кросс-слойный дизайн |
| `analyst` | анализ макетов Claude Design `52adb1f7-312b-4f25-8c47-2bccfca9df94` |
| `designer` | дизайн-система и стили |
| `dotnet-builder` | сборка и починка .NET |

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

- Хранилище проектов: `data/projects.json` рядом с executable
- Метаданные сессий персистятся в `data/sessions.json`, история чата — `data/sessions/{claudeSessionId}/history.json`; процессы claude in-memory, resume через `--resume <claude-session-id>`
- Временные чаты: `Session.ExpiresAfterMinutes` (null — обычный чат), тумблер + пресеты срока в «Настройках чата»; `ChatExpiryService` (тик 60с) удаляет чаты, неактивные дольше срока (кроме статусов Working/Waiting); `DeleteAsync` чистит историю на диске и шлёт `chat_deleted`
- Path traversal защита: `FileService.SafeJoin` — все пути через неё
- git diff/revert через `git` CLI; если не git-репо — возвращает null
- Комментарии в коде по-русски
