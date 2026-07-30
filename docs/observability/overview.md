# Observability ClaudeHomeServer

**OpenTelemetry-based** observability с двумя режимами: dev (Aspire Dashboard) и
production (SigNoz поверх ClickHouse).

Эта документация — центральная точка входа. Детали аудита существующих поверхностей и
runbook развёртывания SigNoz вынесены в отдельные файлы (см. [Cross-links](#cross-links)),
здесь — только scope, архитектура, границы и решения.

## Scope

**В этом плане:** OTel SDK instrumentation (traces + metrics) с two-mode конфигурацией
(dev / production / both), экспорт через OTLP, санация PII на стороне приложения до
экспорта, типизированный фасад метрик с allowlist для защиты кардинальности.

**НЕ входит (отдельные epic'и):**

- **Alerting** (email/Telegram уведомления) — отложено, см. [Future Epics](#future-epics-explicitly-deferred).
- **Persistent postmortem beyond SigNoz** (Tempo/Loki) — N/A: SigNoz + ClickHouse уже даёт
  персистентное хранение (30d traces / 90d metrics, см.
  [observability-signoz-setup.md](signoz-setup.md#retention-срок-хранения)).
- **End-user-facing telemetry UI** — аудитория телеметрии = администратор, не конечный
  пользователь.
- **Per-user metric attribution** — соображения приватности: только атрибут
  `deployment.environment` различает инстансы, атрибуты пользователя в метрики не попадают
  (см. [Privacy](#privacy-pii)).
- **WebDavHandler instrumentation** — вне MVC pipeline, требует отдельного wrapper'а,
  будущий epic.
- **ProjectEventLogService merge** — комплементарно, не замена: OTel = операционные метрики,
  event log = доменный аудит (см. [Duplication Architecture](#duplication-architecture)).

## Архитектура

```
                    ClaudeHomeServer (ASP.NET Core 10)
                    ├── OTel SDK (traces + metrics)
                    │   └── PiiSanitizingProcessor  (span attrs до экспорта)
                    │   └── ServerMetrics            (typed facade, cardinality allowlist)
                    │
                    │ OTLP/gRPC :4317  ──┐
                    │ OTLP/HTTP :4318  ──┤
                    ▼                    ▼
            ┌──────────────┐    ┌──────────────────┐
            │ Aspire       │    │ SigNoz           │
            │ Dashboard    │    │ (otel-collector → │
            │ (in-memory)  │    │  ClickHouse)     │
            │ ~110 min     │    │  30d / 90d       │
            └──────────────┘    └──────────────────┘
             dev mode            production mode
```

**Три режима** (конфигурация через `appsettings.Local.json`, секция `Telemetry`):

| Режим | Backend | Назначение |
|---|---|---|
| `dev` | Aspire Dashboard | In-memory, для живого дебага при разработке |
| `production` | SigNoz (ClickHouse) | Персистентное хранение, дашборды, запросы |
| `both` | Aspire + SigNoz (fan-out) | Локальный дебаг + продакшн-копия одновременно |

## Backends

### SigNoz (production)

- **Хранилище:** ClickHouse (персистентный volume).
- **Retention:** 30 дней traces / 90 дней metrics (TTL в таблицах ClickHouse).
- **Встроенные дашборды** и query-режим поверх traces/metrics.
- **UI:** http://localhost:3301
- **Развёртывание и troubleshooting:** [docs/observability/signoz-setup.md](signoz-setup.md)

### Aspire Dashboard (dev, optional)

- **Хранилище:** in-memory, окно трейсов ~110 минут.
- **Live debugging UI** без настройки персистентности.
- **Setup:** отдельный docker-compose (TODO когда добавим).

## Duplication Architecture

Подробный аудит существующих поверхностей: [docs/observability/audit.md](audit.md).

**Принцип (вывод из аудита):** OTel добавляет **операционный слой** (latency, error rates,
rate-limiting), а не дублирует существующие доменные сторы. SpendStore остаётся source of
truth для billing/accounting — метрики токенов и стоимости в OTel не добавляются (**C4**).

| Данные | Source of truth | OTel? | Обоснование |
|---|---|---|---|
| Токены (input/output/cache) | SpendStore JSONL | ❌ НЕТ | C4 — не дублировать billing |
| Стоимость ($) | SpendStore JSONL | ❌ НЕТ | C4 — не дублировать billing |
| Длительность LLM (per-turn) | SpendStore `DurationMs` | ✅ агрегат | OTel = histogram p50/p99, разный use case |
| LLM-вызовы модулей | ModuleLlmUsageStore | ⚠️ только rate | Разные слои: доменный аудит vs операционные метрики |
| MCP-вызовы | McpCallLog (in-memory) | ⚠️ семантически разные | McpCallLog = live диагностика до рестарта, OTel = персистентный экспорт |
| Доменные события | ProjectEventLogService (SQLite) | ❌ НЕТ | Структурированные события vs метрики — не пересекаются |

## Privacy (PII)

**Архитектурное решение AD6:** атрибуты и спанов, и логов проходят через санитайзер перед
экспортом. Оба бэкенда (Aspire и SigNoz) получают уже очищенные данные — санация на стороне
приложения, не на коллекторе.

Правила общие для обоих сигналов и живут в одном месте — `PiiRules`. Имена сравниваются
нормализованно (без разделителей, регистронезависимо), поэтому тег спана `session_id`
и параметр лога `{SessionId}` подчиняются одному правилу. Иначе стиль записи решал бы,
утечёт PII или нет.

| Attribute | Action | Почему |
|---|---|---|
| `file_path`, `*.path`, `url.path` | Hash → `sha256(value)[..8]` | Путь может содержать имя проекта/пользователя/файла |
| `persona_name`, `persona.id` | DROP | Идентификатор персоны = PII |
| `user_id`, `owner_id` | DROP | Идентификатор пользователя = PII |
| `prompt`, `text`, `content`, `body`, `message` | DROP | Тело запроса/ответа = PII |
| `url.full`, `url.query` | DROP | В query-строке уезжают API-ключи (Dify, OpenRouter) — **C6** |
| `session_id`, `turn_id` (GUIDs) | KEEP | Неидентифицирующие, нужны для корреляции трейсов |
| `provider`, `model`, `direction` | KEEP | Операционные, не PII |
| `tool_name`, `outcome`, `error_type`, `reason` | KEEP | Операционные, не PII |
| `http.request.method`, `http.response.status_code`, `http.route`, `server.address` | KEEP | Стабильные semconv-имена, без пользовательских данных |
| Unknown tags | DROP (default deny) | Белый список — всё незнакомое отбрасывается |

**Спаны** (`PiiSanitizingProcessor`): дополнительно очищается `StatusDescription` —
инструментация кладёт туда текст исключения с URL и путями сборки. По той же причине
выключен `RecordException`: он пишет `exception.message`/`exception.stacktrace`
в `activity.Events`, а события — неизменяемая коллекция, санитайзер до них не дотянется.

**Логи** (`PiiSanitizingLogProcessor`): тело сообщения возвращается к ШАБЛОНУ. Вместо
«Временный чат abc «Отчёт по клиенту» удалён» уезжает «Временный чат {SessionId} «{Name}»
удалён» — событие остаётся понятным, значения не уезжают. Это необходимо, потому что
экспорт идёт с `IncludeFormattedMessage` и `ParseStateValues` (нужны для читаемости
в SigNoz ListView).

> **Остаточный риск:** сообщение, записанное интерполяцией (`$"...{value}"`), шаблона
> не имеет — подставленная строка и есть шаблон, вычистить из неё нечего. Логировать
> следует структурно: `logger.LogInformation("Чат {SessionId} удалён", id)`.

**Implementation:** `Telemetry/PiiRules.cs` (правила), `PiiSanitizingProcessor.cs` (спаны),
`PiiSanitizingLogProcessor.cs` (логи). Тесты — `PiiSanitizerTests`, `PiiLogSanitizerTests`.

## Cardinality Guardrails

**Архитектурное решение AD5 / C5:** типизированный фасад `ServerMetrics` запрещает ad-hoc
теги. Все теги проходят через typed methods с allowlist — добавить произвольный
high-cardinality тег (например, `user_id` или `file_path`) невозможно: компилятор и тесты
не дадут.

**Allowlist тегов:** `{provider, model, execution, tool_name, outcome, error_type, reason}`.

**`execution` — песочница или хост.** Значений ровно два (`local`/`docker`), берутся из кода
(`TurnTelemetry.ExecutionKind` по `IProcessLauncher.IsSandboxed`), ограничитель значений им не
нужен. Тот же словарь пишется в тег `kind` спана `process.start` — намеренно один, иначе трейс
и метрику не сопоставить при разборе «песочница тормозит».

Ось не инстансная, а **ходовая**: среду выбирает `ILauncherFactory.ForOwner` по полю
`User.ExecutionEnvironment` ВЛАДЕЛЬЦА процесса, поэтому один и тот же инстанс порождает и
хостовые, и песочные ходы. При нескольких пользователях разрез читается как «чьи ходы», а не
«контейнер медленный» — это оговорено в описании панелей.

**Имена тегов ≠ значения.** Allowlist закрывает только имена: он не даёт завести
`user_id`, но ничего не говорит о том, сколько разных значений приедет в разрешённый тег.
Два значения приходят снаружи и замкнутыми множествами не являются:

| Тег | Источник | Чем грозил |
|---|---|---|
| `tool_name` | заголовок `X-Mcp-Tool` от MCP-сервера | без заголовка вместо имени инструмента подставлялся **путь запроса** — `/api/projects/{guid}/files/…`: и взрыв кардинальности, и PII в метрике (санитайзер сидит только в pipeline трейсов и метрик не видит) |
| `model` | ответ CLI (`system/init`, `message.model`), фолбэк — `Session.Model` | свободный пользовательский ввод: любая строка = новый временной ряд |

**Модель берётся из ответа CLI, а не из настроек чата.** `Session.Model` и слоты тиров —
это НАМЕРЕНИЕ: когда модель у чата не задана и слот пуст, резолвер отдаёт null («решает
CLI»), и в тег уходил литерал `unknown`. На боевом ходе так и вышло — спан приехал с
`model: unknown` при живом ответе на 8.9 секунды, то есть панель «какими моделями считаем»
отвечала «не знаю». Факт называет сам CLI: в событии `system/init` (поле `model`) и в каждом
`assistant` (`message.model` — модель, выдавшая конкретный ответ; она точнее). Разбор —
`TurnTelemetry.ModelFromEvent`, намерение осталось фолбэком на случай, если CLI модель не
назвал. В спане `chat.turn` тег ставится дважды: намерением при старте хода и фактом, когда
CLI его сообщит.

**Ограничитель значений** — `Telemetry/MetricTagGuard.cs`, вызывается внутри `ServerMetrics`
(единственная точка записи, мимо не пройти). Две ступени:

1. **Форма.** `tool_name` — латиница, цифры, `_ - .` (путь отсекается слэшем, «(без имени) …» —
   скобками и пробелом); `model` — то же плюс `:` и `/` (теги Ollama, `direct:`-маршруты
   OpenRouter). Длина ≤ 64.
2. **Потолок различных значений** — 256 для инструментов (реальных ≤ 80–90), 64 для моделей.

Не прошедшее схлопывается в `other`, отсутствующее — в `unnamed`. Счётчик вызовов при этом
не теряется, теряется только детализация; точные значения остаются в диагностике
(`GET /api/mcp/calls` для инструментов, транскрипт и SpendStore для моделей).

Каждый новый ряд в ClickHouse живёт до конца retention, поэтому одна такая утечка портит
стор надолго — вычищать приходится мутациями по таблицам.

**Тесты-стражи:**

- `MetricTagAllowlistTests.cs` — имена тегов: падает при попытке добавить forbidden tag.
- `MetricTagGuardTests.cs` — значения: путь запроса и свободный текст модели не должны
  доезжать до измерения (проверяется через `MeterListener`, а не вызовом чистой функции).

## Sampling Strategy

Сэмплинг на стороне приложения (до экспорта), конфигурируется через
`Telemetry:TraceSampleRatio:{Dev,Production}`. **Дефолт — `1.0`, пишем все трейсы.**

Прежние 0.10 / 0.05 были взяты из практики нагруженных сервисов и здесь работали против
цели: инсталляция однопользовательская, ходов единицы в минуту, и при 5% нужного трейса
в 19 случаях из 20 просто нет — «трейсинг включён, а разобрать по нему нечего». Экономить
не на чем: такой поток 15-дневный retention переваривает не замечая, а метрики сэмплинг
вообще не затрагивает. Понижать имеет смысл, только если поток спанов реально станет
тяжёлым.

| Значение | Sampler | Смысл |
|---|---|---|
| не задано / `1.0` | `AlwaysOn` | пишем все трейсы (дефолт) |
| `0` | `AlwaysOff` | трейсы не нужны — **раньше это молча превращалось в дефолт**, то есть выключить трейсинг конфигом было нельзя |
| `(0;1)` | `ParentBased(TraceIdRatio(x))` | доля корневых трейсов |
| вне `[0;1]` | `AlwaysOn` + жалоба в stderr | мусор в конфиге не должен ни ронять старт, ни тихо гасить трейсы |

`ParentBased` гарантирует, что дочерние спаны следуют решению родителя — распределённый
трейс не обрывается на середине.

**Адрес коллектора.** `OtlpEndpoint` проверяется перед использованием: значение без схемы
(`localhost:4318`) или с чужой схемой выключает экспорт с сообщением в stderr. Раньше строка
шла в `new Uri(...)` без проверки, и опечатка в конфиге роняла приложение на старте —
единственное место, где observability убивала продукт.

## Гейджи сессий

| Метрика | Что считает |
|---|---|
| `ccs.sessions.active` | сессии, которые сейчас работают или ждут человека (`SessionLiveness.IsLive` — тот же предикат, что у сводки главной) |
| `ccs.sessions.total` | всего чатов в реестре `SessionManager` — размер стора, не активность |
| `ccs.websocket.connections` | живые SignalR-соединения |

Разделены не для полноты: под именем `ccs.sessions.active` раньше отдавался размер реестра
(`SessionManager.ActiveCount`), то есть все чаты, поднятые из `sessions.json` при старте.
График показывал сотни, не падал после рестарта и не реагировал на работу. **Ряд
`ccs.sessions.active` до этой правки означал другое — сравнивать его с новыми точками
нельзя.**

## Resource Attributes

Standard OTel resource attributes на каждый экспортируемый сигнал:

| Attribute | Значение | Источник |
|---|---|---|
| `service.name` | `"ClaudeHomeServer"` | Константа |
| `service.version` | (из сборки) | `Assembly.GetName().Version` |
| `service.instance.id` | `MachineName` | `Environment.MachineName` |
| `deployment.environment` | `"dev"` / `"prod"` / `"sandbox"` | `Telemetry:Environment` из конфига |

**Multi-instance:** каждый инстанс ClaudeHomeServer на машине шлёт на общий SigNoz.
Различаются через `deployment.environment` — это единственный атрибут, различающий инстансы
(см. [Scope — per-user attribution отключена](#scope)). Атрибутов пользователя в resource
нет (приватность).

## Алертинг

Метрики собираются и рисуются на дашборде, но пока о проблеме никто не узнаёт, её
не существует. Алертинг закрывает разрыв: SigNoz оценивает правила, а CCS превращает
загоревшиеся алерты в обычные уведомления — колокол, тост и **push на PWA**.

```
SigNoz (правила как код: docker/observability/alerts/*.json)
   │  GET /api/v1/alerts  ← опрос раз в 60 с
   ▼
AlertPollingService  →  NotificationService  →  колокол + тост + web push
                                                          ↓
СТРАХОВКА, минуя CCS:  правило «Пульс телеметрии пропал» → email-канал SigNoz
```

### Почему опрос, а не webhook

Направление «SigNoz → CCS» упирается в боевой хост: он слушает **HTTPS на порту 80**
с сертификатом на `grisha.naychenko.me`, а запрос из контейнера идёт на
`host.docker.internal`. TLS падает по SNI, а при отключённой проверке http.sys отвечает
`400 Bad Request — Invalid Hostname`. Обратное направление (CCS опрашивает SigNoz) уже
работает и не требует ни публичного эндпоинта, ни общего секрета, ни возни с сертами.
Цена — задержка до минуты, для этих правил несущественная.

### Что рассылает и кто получает

Рассылает **только тот инстанс, где включено** (`Telemetry:Alerts:Enabled`): push-подписки
лежат в `data/` каждого инстанса отдельно, поэтому доставить на телефон может лишь тот,
на который подписана PWA — обычно боевой. Получатели — пользователи с ролью `admin`.

| Событие | Куда | Будит? |
|---|---|---|
| Алерт загорелся | колокол + тост + push, категория «Алерт» (⚠) | да |
| Алерт погас | колокол + тост, категория «Выполнено» | нет |
| Загорелось больше пяти сразу | одно сводное уведомление | да |

### Ловушки формата (сняты с живого стенда)

Схема `/api/v1/alerts` не задокументирована, и две её особенности ломают «очевидную»
реализацию — обе покрыты тестами в `AlertDigestTests`:

- **Одно правило порождает несколько алертов** — по одному на серию разреза. Правило
  с `groupBy` по `deployment.environment` даёт отдельные алерты для dev и production
  с разными `fingerprint`. Дедупликация по имени схлопнула бы их в одно событие,
  и о боевом контуре не сообщили бы, пока шумит дев.
- **`endsAt` лежит в БУДУЩЕМ** у горящего алерта и продлевается на каждом цикле
  (наблюдалось `startsAt 14:27` при `endsAt 14:31`). Признак «починилось» — это
  ИСЧЕЗНОВЕНИЕ из выдачи, а не наступление `endsAt`.

Отдельно: неудачный опрос возвращает `null`, а не пустой список. Вернув при обрыве связи
пустоту, мы разослали бы «всё восстановилось» ровно в тот момент, когда потеряли SigNoz
из виду.

### Правила

Живут в репе как код (`docker/observability/alerts/*.json`), накатываются идемпотентным
`apply-alerts.ps1` — как и дашборды. Стартовые пороги подобраны «чтобы не молчать и не
выть» и будут уточняться после первой недели наблюдений.

| Правило | Метрика | Порог |
|---|---|---|
| Пульс телеметрии пропал | `ccs.telemetry.heartbeat` | нет данных 15 мин |
| Всплеск ошибок LLM | `ccs.llm.errors` | > 5 за 10 мин |
| Ходы стали медленнее | `ccs.llm.duration.bucket` p99 | > 300 000 мс за 15 мин |
| Отказы MCP-инструментов | `ccs.mcp.errors` | > 3 за 15 мин |
| Сбой синхронизации знаний | `ccs.dify.sync.errors` | > 3 за 15 мин |

> **Канал обязателен.** SigNoz отказывается создавать правило без канала уведомлений
> («at least one channel is required») — даже когда доставка идёт опросом. Поэтому
> в репе правила ссылаются на канал `ccs-alerts-email`; его надо создать один раз,
> иначе импорт правил падает. Он же служит страховкой для «пульса»: если лежит сам CCS,
> push отправлять некому, а SigNoz пошлёт письмо сам.

### Настройка

```jsonc
"Telemetry": {
  "Alerts": {
    "Enabled": true,                    // на деве обычно false
    "SignozUrl": "http://localhost:3301",
    "ApiKey": "<service account key>",
    "PollSeconds": 60
  }
}
```

Выключено или без ключа — служба не поднимается вовсе. Состояние разосланного лежит
в `data/alert-state.json` и переживает перезапуск: повторять старые тревоги после
рестарта не нужно.

## Cross-links

- [Аудит существующих поверхностей](audit.md) — что уже есть в проекте, чтобы
  не дублировать. Сводная таблица 4 сторов + cross-reference планируемых OTel-метрик.
- [SigNoz setup runbook](signoz-setup.md) — развёртывание stack, retention,
  порты, troubleshooting, объём диска.
- [SigNoz-дашборды](dashboards.md) — дашборды как IaC: JSON в репе,
  идемпотентный импорт через `apply.ps1`, backup-стратегия.
- [MCP-servers docs](../architecture/mcp-servers.md) — диагностика MCP-вызовов через `GET /api/mcp/calls`
  (in-memory счётчики, дополняют OTel).
- [CLAUDE.md](../../CLAUDE.md) — общая архитектура проекта, REST API, соглашения.

## Future Epics (explicitly deferred)

Эти элементы сознательно вынесены за scope текущего плана. Каждый — отдельный epic со
своим ADR.

1. **WebDavHandler instrumentation** — вне MVC pipeline, нужен custom `IMeter` wrapper
   вокруг WebDav-обработчиков. Отдельная проработка surface area.
2. **Persistent postmortem** — если SigNoz + ClickHouse не хватит для deep-dive расследований,
   Tempo/Loki stack. Сейчас N/A: 30d traces уже персистентны.
3. **End-user UI** — если понадобится user-facing observability (не только админ).
   Требует privacy review: текущий scope = admin only, per-user attribution отключена.
