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
 │    ├── Llm/                слой LLM-адаптеров (см. раздел «LLM-адаптеры»)
 │    │    ├── ILlmSessionAdapter + LlmSessionAdapterFactory (провайдер из Session.Model)
 │    │    ├── Claude/ClaudeSession   Process-обёртка claude.exe
 │    │    └── DeepSeek/DeepSeekSession  HTTP/SSE + свой tool-цикл
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

## LLM-адаптеры (Services/Llm)

Работа с моделью — за интерфейсом `ILlmSessionAdapter` (калька публичного контракта
ClaudeSession + `LlmCapabilities`). `SessionManager` создаёт адаптер через
`LlmSessionAdapterFactory`; провайдер вычисляется из `Session.Model`
(`LlmProviderResolver`: `deepseek*` → DeepSeek, иначе Claude) и не персистится.
`Session.ClaudeSessionId` — generic id сессии у провайдера (Claude — транскрипт CLI
для `--resume`, DeepSeek — GUID истории). Смена провайдера у начатой сессии — 400.

- **Claude** (`Llm/Claude/ClaudeSession`) — subprocess claude.exe (см. следующий раздел);
  `ClaudeCliLocator` — общий поиск claude.exe.
- **DeepSeek** (`Llm/DeepSeek/`) — официальный API (OpenAI-совместимый SSE,
  `DeepSeekClient`), собственный tool-цикл (`DeepSeekSession`): инструменты
  read_file/list_dir/grep_search/write_file/edit_file поверх `FileService.SafeJoin`
  (`DeepSeekTools`), permissions через те же `PermissionRequestMessage` (общий
  `PermissionRuleEvaluator` + маппинг Mode), история messages[] в
  `data/sessions/{id}/deepseek-messages.json` (`DeepSeekConversationStore`, resume
  после рестарта). reasoning_content в историю НЕ возвращается (API 400).
  Конфиг — секция `DeepSeek` (ApiKey в appsettings.Local.json — без него провайдер
  выключен и модели скрыты; список моделей строго из `DeepSeek:Models` — алиасы
  deepseek-chat/reasoner выведены 24.07.2026, актуальны deepseek-v4-flash/pro, окно 1M).

Возможности провайдера (`LlmCapabilities`: plan/compact/mcp/effort/…) отдаются фронту
в блоке `providers` из `GET /api/models` и в `session_started`; UI скрывает недоступное
(`useModelCaps` в `lib/models.ts`). Общие хелперы адаптеров: `TurnFileWatcher`
(file_changed на время хода), `AttachmentInliner` (инлайн вложений).

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
- Продуктовая история «Что нового» (основной функционал, кнопка в шапке): AI-сводка изменений **по всем проектам сразу** — что нового и чем полезно пользователю (не код, не diff). [ChangelogService](backend/ClaudeHomeServer/Services/ChangelogService.cs) собирает git-коммиты из репы продукта — путь в `Changelog:SourceRepoPath` (машинно-специфичный, в `appsettings.Local.json`; без него раздел показывает «не настроено»), имя — `Changelog:SourceProjectName` (дефолт = имя папки). Дальше группирует по дням и суммирует каждый день через claude CLI (модель `Changelog:Model`, дефолт haiku; 1 вызов на день, лениво, продуктовый промпт — польза, а не техника). Каждый пункт: `type` (feature/improvement/fix/other), `area` (раздел продукта — Claude определяет сам), `emoji`, `title`, `benefit`, `authors`, `projects`. Результат кешируется на уровне продукта в `data/changelog/product.json` (ключ дня = хеш sha-набора всех проектов — сводка одна для всех и перегенерируется только при новых коммитах дня). Алиасы авторов — `Changelog:AuthorAliases` (email → имя). Эндпоинты — глобальный [HistoryController](backend/ClaudeHomeServer/Controllers/HistoryController.cs) (`api/history/*`). Фронт: [ProductHistory.tsx](frontend/src/components/ProductHistory.tsx) — полноэкранная лента по дням (Сегодня/Вчера/дата). Внутри дня пункты **сгруппированы по области** (`area`) в виде **вкладок-подчёркиваний** (переключаются, активная с accent-линией); в активной области — таймлайн пунктов с маркерами-кружочками (единый accent-цвет). Иконки авторов — роли (`AUTHOR_EMOJI`: Григорий 🧑‍💼, Андрей 👨‍💻; новые — из пула детерминированно по имени). Фильтр по исполнителю (чипы, авторы по алфавиту). Дни **сворачиваемые**: по умолчанию раскрыт только последний (`DEFAULT_EXPANDED`), он и генерится; остальные свёрнуты и генерятся лениво по клику. Кнопка «Что нового» в [HubHeader](frontend/src/components/HubHeader.tsx) видна во всех разделах (событие `open-product-history` → overlay в [App.tsx](frontend/src/App.tsx)), бейдж считает новые коммиты с последнего захода (timestamp в `localStorage`)
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
- Сессии только in-memory; resume через `--resume <claude-session-id>`
- Path traversal защита: `FileService.SafeJoin` — все пути через неё
- git diff/revert через `git` CLI; если не git-репо — возвращает null
- Комментарии в коде по-русски
