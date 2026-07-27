# Аудит observability-поверхностей ClaudeHomeServer

Дата: 2026-07-27
Назначение: контекст для интеграции OpenTelemetry (предотвращение дублирования источников данных).

## Сводная таблица

| Поверхность | Хранилище | Гранулярность | Потребитель | Query pattern | Retention |
|---|---|---|---|---|
| SpendStore | JSONL файлы + JSON агрегаты (`data/spend/`) | Per-turn (детально) / per-day (агрегат) | Billing, аналитика (пользователь + админ) | In-memory dict scan (детали) + JSON десериализация (агрегаты) | Детали: `DetailDays` (дефолт 30 дней, сворачиваются в агрегаты); агрегаты: бессрочно |
| ModuleLlmUsageStore | JSONL файл (`data/module-llm-usage.jsonl`) | Per-call (каждый вызов LLM-канала модуля) | Доменный аудит (контракт модулей §10.5) | Append-only файл; чтение не реализовано в текущем коде | Бессрочно (рост не ограничен) |
| McpCallLog | In-memory (`ConcurrentDictionary` + `ConcurrentQueue`) | Per-call (каждый HTTP-запрос MCP-сервера к бэкенду) | Операционная диагностика (админ) | In-memory: итерация по dict + reverse по queue | До рестарта процесса (ring buffer 200 сбоев, счётчики — бесконечно) |
| ProjectEventLogService | SQLite (`data/project-events.db`) | Per-event (каждое значимое действие в проекте) | Пользовательская лента активности, дайджест | SQL: индекс по `(owner_id, project_id, ts DESC)` | 90 дней (ленивый prune раз в 24 ч) |

## Подробно по каждой

### SpendStore

- **Назначение**: учёт расхода токенов и стоимости по всем LLM-вызовам (ходы чатов, one-shot фоновые действия, fal.ai генерации, бесплатные модели). Source of truth для billing/accounting.
- **Хранилище**: гибрид файловое хранилище в `data/spend/`:
  - `turns-YYYY-MM-DD.jsonl` — детальные записи, append-only (строка = JSON `SpendRecord`)
  - `daily.json` — дневные агрегаты по полному составному ключу разрезов (JSON-словарь `date → List<DailySpendRow>`)
  - `backfill.done` — маркер разового импорта истории
- **Гранулярность**: per-turn (детально, последние `DetailDays` дней) / per-day (агрегат, старше `DetailDays`)
- **Поля** (`SpendRecord`, `backend/ClaudeHomeServer/Models/SpendRecord.cs:28`):

  | Поле | Тип | Описание |
  |---|---|---|
  | `Id` | `string` (GUID N, init) | Детерминированный ID записи |
  | `Timestamp` | `DateTime` (UTC) | Момент записи |
  | `OwnerId` | `string` | Владелец (пусто = системный вызов) |
  | `ProjectId` | `string?` | Проект |
  | `SessionId` | `string?` | Чат/сессия |
  | `TaskId` | `string?` | Задача |
  | `PersonaId` | `string?` | Персона |
  | `Provider` | `string` | Провайдер ("claude", "ollama", "deepseek" и т.д.) |
  | `Model` | `string?` | Модель |
  | `Source` | `string` | Источник: `chat-turn`, `one-shot`, `fal`, `free` |
  | `InputTokens` | `long` | Входные токены |
  | `OutputTokens` | `long` | Выходные токены |
  | `CacheReadTokens` | `long` | Токены из кэша (cache read) |
  | `CacheCreationTokens` | `long` | Токены записи в кэш (cache creation) |
  | `CostUsd` | `double?` | Стоимость в USD |
  | `Generations` | `int` | Счётчик генераций fal.ai |
  | `DurationMs` | `long` | Длительность вызова (мс) |
  | `Label` | `string?` | Подпись операции (ключ фонового действия или endpoint fal) |

- **Потребитель**:
  - UI: бейдж стоимости чата (`CostBadge`), виджет «Домой» (`SpendWidgetDto`), дашборд аналитики (pivot по 7 разрезам: user, project, chat, persona, provider, model, source)
  - Биллинг/accounting: единственный правильный источник данных о расходе токенов и стоимости
- **Query pattern**: in-memory — `ConcurrentDictionary<string, List<SpendRecord>>` для деталей, `volatile Dictionary` для агрегатов. Читатели итерируют снимки без блокировок. Чтение с диска только при `Load()` на старте и при rollup.
- **Retention**: детали — `Spend:DetailDays` дней (дефолт 30, конфигурируемо), после чего `RollupOlderThan` сворачивает день в `DailySpendRow` и удаляет jsonl. Агрегаты (`daily.json`) — бессрочно растут. Инвариант: день живёт ЛИБО в деталях, ЛИБО в daily (двойного счёта нет).
- **Source of truth для**: billing/accounting (учёт токенов, стоимость, продолжительность per-turn).
- **Дедуп**: по `Id` — прерванный backfill при рестарте пишет те же детерминированные Id, дубли не проходят (`_ids` dict).
- **Код**:
  - `backend/ClaudeHomeServer/Services/Spend/SpendStore.cs:25`
  - `backend/ClaudeHomeServer/Models/SpendRecord.cs:28`
  - `backend/ClaudeHomeServer/Services/Spend/SpendAnalyticsService.cs:60` (слой аналитики над стором)
  - `backend/ClaudeHomeServer/Services/Spend/SpendAccess.cs:7` (гейт доступа: не-админ видит только свои данные)

### ModuleLlmUsageStore

- **Назначение**: учёт вызовов LLM-канала модулей по контракту §10.5 (ТЗ R13) в разрезе `(moduleId, action, sub)`. Факт каждого вызова (включая неудачные) с маршрутом; токены и стоимость — только когда их отдал провайдер. Тело промпта не сохраняется (приватность).
- **Хранилище**: JSONL файл `data/module-llm-usage.jsonl`, append-only. Каждая строка — JSON `Entry` record.
- **Гранулярность**: per-call (каждый вызов LLM-канала модуля).
- **Поля** (`Entry`, `backend/ClaudeHomeServer/Services/Modules/ModuleLlmUsageStore.cs:15`):

  | Поле | JSON-имя | Тип | Описание |
  |---|---|---|---|
  | `At` | `at` | `DateTime` | Момент вызова |
  | `ModuleId` | `moduleId` | `string` | Идентификатор модуля |
  | `Action` | `action` | `string` | Действие (LLM-ключ) |
  | `Sub` | `sub` | `string` | Подключение (sub) |
  | `Route` | `route` | `string` | Маршрут (локальная модель / прямой адаптер / Claude) |
  | `Ok` | `ok` | `bool` | Успешность вызова |
  | `DurationMs` | `durationMs` | `long` | Длительность вызова (мс) |
  | `Model` | `model` | `string?` | Модель (null если не применимо) |
  | `InputTokens` | `inputTokens` | `long?` | Входные токены (null если провайдер не дал) |
  | `OutputTokens` | `outputTokens` | `long?` | Выходные токены (null если провайдер не дал) |
 | `CostUsd` | `costUsd` | `double?` | Стоимость (null если не применимо) |
  | `Outcome` | `outcome` | `string` | Исход: `ok`, `unavailable`, `timeout`, `too_many`, `declared`, `unknown` |

- **Потребитель**: доменный аудит (контракт модулей §10.5). Текущий код не содержит читателя — только `Record()` пишет в файл. Ресурс `/api/host/**` через `HostLlmController` пишет записи.
- **Query pattern**: append-only файл. Чтение из кода не реализовано — аудитный журнал для внешнего анализа (grep/jq).
- **Retention**: бессрочно (файл растёт, prune/rotation не реализован).
- **Source of truth для**: none (аудитный журнал модульных LLM-вызовов, автономен от SpendStore).
- **Код**: `backend/ClaudeHomeServer/Services/Modules/ModuleLlmUsageStore.cs:13`

### McpCallLog

- **Назначение**: диагностика продуктовых MCP-серверов (`mcp/*`). Показывает сколько раз вызван каждый инструмент, доля отказов и последние сбои. Создан после инцидента: разбор жалобы «инструменты отваливаются» вёл вручную по `data/sessions/*/history.json` за 288 сессий — на бэкенде следа вызовов не было.
- **Хранилище**: in-memory (`backend/ClaudeHomeServer/Services/Mcp/McpCallLog.cs:17`):
  - `ConcurrentDictionary<string, ToolCounters>` — счётчики по имени инструмента (`Calls`, `Failures`, `TotalMs`)
  - `ConcurrentQueue<McpCallFailure>` — кольцевой буфер последних 200 сбоев
- **Гранулярность**: per-call (каждый HTTP-запрос MCP-сервера к бэкенду).
- **Поля счётчиков** (`ToolCounters`):

  | Поле | Тип | Описание |
  |---|---|---|
  | `Calls` | `long` | Общее количество вызовов |
  | `Failures` | `long` | Количество отказов (HTTP 4xx/5xx) |
  | `TotalMs` | `long` | Суммарное время (для среднего) |

- **Поля сбоев** (`McpCallFailure`, `backend/ClaudeHomeServer/Services/Mcp/McpCallLog.cs:64`):

  | Поле | Тип | Описание |
  |---|---|---|
  | `At` | `DateTime` | Момент сбоя |
  | `Tool` | `string` | Имя инструмента |
  | `SessionId` | `string?` | Сессия, из которой шёл вызов |
  | `Path` | `string` | HTTP-путь запроса |
  | `StatusCode` | `int` | HTTP-статус |
  | `ElapsedMs` | `long` | Длительность (мс) |

- **Механика записи** (`McpCallLogMiddleware`, `backend/ClaudeHomeServer/Services/Mcp/McpCallLogMiddleware.cs:15`):
  - ASP.NET middleware, срабатывает по наличию заголовка `X-Caller-Session-Id`
  - Имя инструмента — из заголовка `X-Mcp-Tool` (ставит каждый MCP-сервер)
  - Успешные вызовы — `LogDebug`, отказы — `LogWarning` с кодом и длительностью
  - Параллельно — `McpCallLog.Record()` для агрегации в `GET /api/mcp/calls`
- **Потребитель**: операционная диагностика (только админ, `GET /api/mcp/calls`). Контроллер `McpCallsController` (`backend/ClaudeHomeServer/Controllers/McpCallsController.cs:18`).
- **Query pattern**: in-memory — итерация по dict + reverse по queue. Без SQL, без файлового скана.
- **Retention**: до рестарта процесса (данные не персистятся). Кольцевой буфер сбоев — 200 записей. Счётчики — бесконечно, но обнуляются рестартом.
- **Source of truth для**: диагностика живучести MCP-инструментов (единственный источник на бэкенде).
- **Код**:
  - `backend/ClaudeHomeServer/Services/Mcp/McpCallLog.cs:17`
  - `backend/ClaudeHomeServer/Services/Mcp/McpCallLogMiddleware.cs:15`
  - `backend/ClaudeHomeServer/Controllers/McpCallsController.cs:18`

### ProjectEventLogService

- **Назначение**: append-only хроника значимых действий в проекте (ходы чатов, задачи, память персон, изменения знаний, заметки, изменения команды). Источник для активности-ленты командного центра, дайджеста и прозрачности фоновой работы.
- **Хранилище**: SQLite (`data/project-events.db`, `backend/ClaudeHomeServer/Services/ProjectEventLogService.cs:16`). Режим WAL, busy_timeout 5000 мс.
- **Гранулярность**: per-event (каждое значимое действие).
- **Схема** (v1, `ProjectEventLogService.cs:52`):

  ```sql
  CREATE TABLE project_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    ts TEXT NOT NULL,
    type TEXT NOT NULL,
    actor TEXT NOT NULL,
    summary TEXT NOT NULL,
    entity_ref TEXT
  );
  CREATE INDEX ix_events_owner_project_ts ON project_events(owner_id, project_id, ts DESC);
  CREATE INDEX ix_events_owner_project_type ON project_events(owner_id, project_id, type, ts DESC);
  ```

- **Поля** (`ProjectEvent`, `backend/ClaudeHomeServer/Models/ProjectEvent.cs:13`):

  | Поле | Тип | Описание |
  |---|---|---|
  | `Id` | `long` | Автоинкремент |
  | `ProjectId` | `string` | Проект |
  | `OwnerId` | `string` | Владелец проекта |
  | `Ts` | `DateTime` (ISO 8601 roundtrip) | Момент события |
  | `Type` | `string` | Тип из `ProjectEventTypes` |
  | `Actor` | `string` | PersonaId / «Роль (Имя)» / «user» / «system» |
  | `Summary` | `string` | Человекочитаемое описание |
  | `EntityRef` | `string?` | Ссылка на сущность (id сессии/задачи/памяти) |

- **Типы событий** (`ProjectEventTypes`, `backend/ClaudeHomeServer/Models/ProjectEvent.cs:26`):
  `chat_turn`, `task_created`, `task_completed`, `task_spawned`, `task_deleted`, `memory_learned`, `knowledge_changed`, `note_changed`, `team_joined`, `team_left`

- **Потребитель**: пользовательская лента активности проекта, дайджест «что сделала команда». Вызовы из `ChatTurnLoggerService`, `TaskManager`, `PersonaManager`, `NotesService`, `DailyBriefingService`, `PersonaMemoryAutolearnService`, `ProjectsController`.
- **Query pattern**: SQL с параметризованными запросами. Два метода:
  - `Query(projectId, ownerId, since?, type?, actor?, limit)` — лента проекта (индекс `ix_events_owner_project_ts`)
  - `QueryByOwner(ownerId, since?, type?, actor?, limit)` — кросс-проектная лента (для дайджеста)
- **Retention**: 90 дней (конфигурируемо через `ProjectEventsRetentionDays`, дефолт 90). Ленивый prune при каждой записи `Append()`, но не чаще раза в 24 ч (`PruneIfDue`). `DELETE FROM project_events WHERE ts < @cutoff`.
- **Source of truth для**: хроника проектных событий (единственный источник).
- **Миграции**: лесенка по `PRAGMA user_version`, текущая версия = 1.
- **Код**:
  - `backend/ClaudeHomeServer/Services/ProjectEventLogService.cs:16`
  - `backend/ClaudeHomeServer/Models/ProjectEvent.cs:13`

## Архитектурное решение (для OTel плана)

**SpendStore JSONL = source of truth для billing/accounting** (учёт токенов, стоимость, продолжительность per-turn).

**Вывод для OTel интеграции**: метрики OTel НЕ должны дублировать поля SpendStore (tokens_in, tokens_out, cost). OTel = live operational observability (latency, error rates, rate-limiting), не бухгалтерия.

### Дополнительные поверхности (вне основного задания)

Помимо 4 основных, в проекте есть ещё одна наблюдаемая поверхность, не попавшая в основной список:

**UsageService** (`backend/ClaudeHomeServer/Services/UsageService.cs:9`):
- Хранилище: JSON файл `data/usage.json` (in-memory + ленивый save)
- Гранулярность: per-snapshot (троттлирован, не чаще 1 раз в 3 мин на одно окно)
- Поля: `limitType`, `utilization`, `status`, `isUsingOverage`, `resetsAt`, `overageStatus`, `overageResetsAt`, `subscriptionKey`
- Retention: 8 дней
- Потребитель: экран usage (лимиты подписки Claude)
- Note: это snapshot-хранилище лимитов, не метрики запросов — OTel-дублирования нет.

## Cross-reference table (для T11)

| OTel метрика (планируемая) | Существующий источник | Дублирование? | Решение |
|---|---|---|---|
| `ccs.llm.duration` | SpendStore.DurationMs | Да, но OTel = aggregated histogram | Оставить (разный use case: live latency vs billing) |
| `ccs.llm.tokens` | SpendStore.InputTokens/OutputTokens/CacheReadTokens/CacheCreationTokens | Дублирование | НЕ добавлять — SpendStore = source of truth |
| `ccs.llm.cost` | SpendStore.CostUsd | Дублирование | НЕ добавлять — SpendStore = source of truth |
| `ccs.llm.errors` | ModuleLlmUsageStore.Outcome (ok/unavailable/timeout/too_many/declared/unknown) | Частичное (только модули) | Добавить: OTel покрывает все LLM-вызовы, не только модульные |
| `ccs.mcp.calls` | McpCallLog.ToolCounters.Calls | Да | Оставить: McpCallLog in-memory, OTel — для экспорта/дашбордов |
| `ccs.mcp.errors` | McpCallLog.ToolCounters.Failures + McpCallFailure ring buffer | Да | Оставить: McpCallLog in-memory до рестарта, OTel — персистентный |
| `ccs.mcp.duration` | McpCallLog.ToolCounters.TotalMs / AvgMs | Да | Оставить: OTel histogram точнее среднего |
| `ccs.project.events` | ProjectEventLogService | Нет (разные данные: structured events vs metrics) | Не дублировать: OTel = metrics, event log = domain audit |
