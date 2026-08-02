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
  [signoz-setup.md](signoz-setup.md#retention-срок-хранения)).
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

### Коллектор не запущен: как это выглядит в логах

Экспорт включён, а SigNoz/Aspire не поднят — штатная ситуация на машине разработчика.
Приложению это не мешает: теряется только телеметрия, поэтому в консоли —
**одна строка Warning** и не чаще раза в 5 минут:

```
warn: ClaudeHomeServer.Telemetry.OtlpExport[0]
      Телеметрия не уходит: OTLP-коллектор http://localhost:4318 недоступен (…)
```

Так было не всегда. Экспортёры берут HttpClient из `IHttpClientFactory` под именами
`OtlpTraceExporter` / `OtlpMetricExporter` / `OtlpLogExporter`, а дефолтное логирование
`Microsoft.Extensions.Http` печатает каждый провалившийся запрос как **Error** с полным
стектрейсом `HttpRequestException`. Экспорт идёт по расписанию (трейсы и логи — раз в 5 с,
метрики — раз в минуту), так что консоль забивалась красными портянками, в которых тонули
настоящие ошибки. `ObservabilityExtensions.QuietDownExportLogging` снимает у этих трёх
клиентов дефолтные логгеры и ставит `OtlpExportHttpLogger` (Warning + троттлинг; успешный
экспорт троттлинг сбрасывает, чтобы следующий сбой был виден сразу).

Нюанс на будущее: логирование этих клиентов включается только когда в DI зарегистрирован
`IHttpClientFactory` — без него экспортёр создаёт HttpClient сам и молчит. В CCS фабрика
есть всегда (YARP, алерты SigNoz), в голом репро её может не быть — и тогда тех самых
Error-портянок не видно, что легко принять за «проблемы нет».

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

> **Дев-тревоги приходят на устройства боевого — так и задумано.** SigNoz отдаёт
> опрашивающему инстансу алерты ВСЕХ контуров, а рассылает их тот единственный, у кого есть
> подписки. Иначе о проблемах дева не узнал бы никто. Контур виден в заголовке
> («… — dev») и в источнике уведомления. Если дев шумит экспериментами, его можно отсечь:
> `Telemetry:Alerts:Environments: ["production"]`. Алерты без метки контура проходят
> фильтр всегда — правило без разреза по среде касается инсталляции целиком.

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

## Сохранённые представления (Saved Views)

Дашборд отвечает на вопрос «что происходит», представление — «дай ту самую выборку,
которую я собираю руками каждый раз». Это сохранённый запрос в Explorer: фильтр,
разрезы и колонки под именем. Живут в репе как код
(`docker/observability/views/*.json`), накатываются `apply-views.ps1`.

| Представление | Раздел | Что отбирает | Когда открывать |
|---|---|---|---|
| Отказавшие ходы | traces | `chat.turn` с `outcome = 'error'` | пришёл алерт «Всплеск ошибок LLM» |
| Медленные ходы | traces | `chat.turn` дольше 30 с | пришёл алерт про деградацию скорости |
| Запуски в песочнице | traces | `process.start` с `kind = 'docker'` | разбор «контейнер тормозит или нет» |
| Предупреждения и ошибки | logs | `severity_text` = Warning/Error | быстрый триаж — 129 значимых строк из ~4200 |
| p99 длительности хода | metrics | p99 `ccs.llm.duration.bucket` по контурам | график деградации скорости |
| Ошибки LLM по типам | metrics | rate `ccs.llm.errors` по `error_type` | всплеск отказов в разрезе причин |

Traces- и logs-представления — прямое продолжение алерта: алерт говорит «что-то не так»,
представление показывает, что именно. Metrics-представления частично дублируют дашборд
«Здоровье сервера» (там те же графики с разрезами) — заведены по запросу как быстрый
доступ из Explorer, но дашборд для метрик остаётся основным инструментом.

> **Отказы в трейсах появились не сразу.** Раньше упавший ход был неотличим от
> успешного: `outcome`/`error_type` жили только в метриках, а статус спана оставался
> `Unset` — то есть дашборд показывал, что отказы есть, а открыть их в Traces Explorer
> было нечем. Теперь `TurnTelemetry.MarkTurnOutcome` ставит на спан тег `outcome`,
> `error_type` и статус `Error`. Представление наполнится с первого отказа после
> обновления инстанса.

### Грабли: колонки List View живут в `extraData`, а не в query

Главная засада, и коварная: представление **сохраняется и открывается в списке**,
сервер на запрос отвечает **200** с валидными данными — а фронт List View рушится уже
при отрисовке результата, показывая красный блок
`500 Cannot use 'in' operator to search for 'key' in service.name`. «500» тут врёт —
это не серверная ошибка, а JS-исключение в браузере. Второй симптом того же корня —
вечное «Retrieving your traces!» при открытии из панели Saved Views на Home.

Причина — **колонки List View задаются НЕ в `compositeQuery`, а в отдельном top-level
поле `extraData`** (JSON-**строка** с `selectColumns`). Без него рендерер сваливается на
строковый дефолт `service.name` и делает по нему `'key' in ...`. Поле `spec.selectFields`
на это НЕ влияет — тупик, на котором легко застрять (его наличие не спасает).

Схема снята с официального `signoz-mcp-server`
([pkg/views/examples.go](https://github.com/SigNoz/signoz-mcp-server/blob/main/pkg/views/examples.go)) —
публичной доки по REST-созданию saved views у SigNoz нет, это clickops-путь. Рабочая форма:

```jsonc
// top-level поле представления (СТРОКА, не объект):
"extraData": "{\"selectColumns\":[{\"name\":\"service.name\",\"signal\":\"traces\"},{\"name\":\"name\",\"signal\":\"traces\"},{\"name\":\"duration_nano\",\"signal\":\"traces\"},{\"name\":\"response_status_code\",\"signal\":\"traces\"}]}",
"compositeQuery": {
  "queryType": "builder", "panelType": "list",
  "queries": [ { "type": "builder_query", "spec": {
    "name": "A", "signal": "traces", "source": "", "stepInterval": 0, "limit": 100,
    "order": [ { "key": { "name": "timestamp" }, "direction": "desc" } ],  // не orderBy, не пусто
    "filter":  { "expression": "name = 'chat.turn' AND outcome = 'error'" },
    "having":  { "expression": "" }
  } } ]
}
```

> Подсказка в `signoz-mcp-server` «extraData … safe to leave \"\"» — **вводит в заблуждение**:
> именно пустой `extraData` роняет List View на дефолтном резолве колонок.

**Отличия по разделам** (`signal` внутри `spec` обязан совпадать с `sourcePage`):

- **logs** (`panelType: list`) — та же засада с `extraData.selectColumns`, что у traces
  (в официальном примере его нет, но без него List View так же падает — колонки задаём
  явно: `timestamp, severity_text, service.name, body`). `order` — два ключа
  (`timestamp`, затем `id` для устойчивой пагинации). Severity фильтруется равенством
  строки: `severity_text = 'Warning' OR severity_text = 'Error'` (значения в наших логах —
  `Information`/`Warning`/`Error`, не `WARN`/`ERROR`).
- **metrics** (`panelType: graph`) — **`extraData` не нужен** (это график, а не таблица
  колонок — тот класс падения там невозможен), но обязателен блок `aggregations`
  (`metricName` + `timeAggregation` + `spaceAggregation`), `stepInterval: 60` и
  `order` по `__result`.

Проверять надо не список и не код возврата запроса, а **применение вьюхи с отрисовкой
результата, причём на ОБОИХ путях**: из выпадашки «Select a view» в Explorer И из панели
Saved Views на Home (последняя восстанавливает вьюху из URL другим кодовым путём и ловит
падение, которого нет в первом). Первые попытки чинились «на глаз» по списку и коду 200,
потом по одному только Explorer — и оба раза пропускали падение на Home-пути. Признак
успеха — нормальное «This query had no results» / «No data» (пустой результат), а не
«Retrieving…» и не красный блок. Проверено на всех шести с Home.

### Грабли: PUT портит представление

`PUT /api/v1/explorer/views/{id}` в SigNoz v0.134 отвечает **200**, но сохраняет запрос
испорченным. Следом ломается не одна запись, а **вся выдача раздела**:

```
GET /api/v1/explorer/views?sourcePage=traces
→ 500  error in unmarshalling explorer query data: invalid character '\'
```

Чинится только удалением битой записи по id — а его после поломки уже не получить из
API, потому что список не читается. Достать можно из метастора:

```powershell
docker exec signoz-metastore-postgres-0 psql -U signoz -d signoz `
  -c "SELECT id, name, source_page FROM saved_views ORDER BY created_at;"
```

Поэтому `apply-views.ps1` обновляет через **DELETE + POST** и никогда не шлёт PUT.
Проверено прямым опытом: тот же файл через POST даёт читаемый список, через PUT —
сломанный.

Ещё одна мелочь того же рода: `GET /api/v1/explorer/views` **без** `?sourcePage=`
возвращает пустой список, а не все представления. Легко решить, что ничего
не создалось, и наплодить дублей.

## Раздел «Телеметрия» в UI (встроенный SigNoz)

SigNoz живёт на `127.0.0.1:3301` (bind к localhost), поэтому «в лоб» его видно только с
хост-машины. Чтобы телеметрия открывалась и удалённо (с телефона через PWA), UI встроен
в CCS отдельным разделом (меню аватара → «Телеметрия», **только у админов**) через
`<iframe>` с **same-origin пробросом**: браузер грузит `/telemetry-proxy/` с нашего
origin, а бэкенд форвардит на локальный SigNoz.

**Как это собрано:**

- **Проброс** — middleware `/telemetry-proxy/**` в [Program.cs](../../backend/ClaudeHomeServer/Program.cs)
  (по образцу preview-прокси, `IHttpForwarder`, WebSocket-upgrade нативно). Аутентификация
  под iframe — cookie `cc_telemetry` (iframe не носит `Authorization`); роль сверяется по
  `UserStore` (не админ → 403, даже с валидной cookie). Выключено в конфиге → 503.
- **base-path** — префикс `/telemetry-proxy` **не срезается**: SigNoz поднят с env
  `SIGNOZ_GLOBAL_EXTERNAL__URL=…/telemetry-proxy` (overlay `docker-compose.observability.yml`,
  через переменную `SIGNOZ_EXTERNAL_URL`) и сам релоцирует SPA под префикс — вставляет
  `<base href="/telemetry-proxy/">`, ассеты и внутренний API резолвятся под ним. Спайк
  подтвердил: корень `/` при этом отдаёт 404 (с SPA-fallback CCS не конфликтует), а
  `/api/v1/health` продолжает отвечать и на корне — поэтому vendored healthcheck трогать
  не пришлось.
- **Статус** — `GET /api/telemetry/status` (`{configured, reachable, proxyPath}`,
  admin-only). Фронт по нему решает: iframe или заглушка «настрой, администратор». На
  ненадёжный iframe `onerror` не полагаемся — статус приходит server-side.
- **Логин** — внутри iframe обычная форма входа SigNoz (свои креды, не CCS). Public
  Sharing / anonymous в Community-редакции нет, автологин не делаем — вход разовый, JWT
  живёт в localStorage нашего origin.

**Включение:**

```jsonc
"Telemetry": {
  "Ui": {
    "Enabled": true,                    // раздел показывает SigNoz; false — заглушка
    "InternalUrl": "http://localhost:3301"  // куда форвардить (порт из overlay)
  }
}
```

Плюс SigNoz должен быть поднят с `SIGNOZ_EXTERNAL_URL` (см. overlay). Забыть env → SPA
не встанет под префикс, iframe будет пустым при живом бэкенде.

### Настройка на новом инстансе (свой отдельный CCS)

Раздел работает per-инстанс: у каждого CCS свой SigNoz (наш bind'ится к `127.0.0.1` и
извне недоступен — общий использовать нельзя). Ниже — как поднять телеметрию на чистом
инстансе. Команды даны напрямую: подразумевается, что у инстанса **нет оркестратора
`ClaudeCodeServerRunner`** — запуск и рестарт CCS выполняются вручную (`dotnet run`,
опубликованный exe или `docker-compose.claude.yml` — как этот инстанс обычно и запускают).

1. **Код с фичей.** Обновить репозиторий до версии, где раздел «Телеметрия» есть
   (`git pull`), пересобрать бэк и фронт.
2. **Поднять свой SigNoz:**
   ```
   docker compose -f docker-compose.observability.yml up -d
   ```
   base-path env (`SIGNOZ_EXTERNAL_URL`) уже в overlay с рабочим дефолтом `localhost` —
   менять не нужно (важен только PATH `/telemetry-proxy`, он фиксирован; ХОСТ в base не
   идёт). При первом заходе на `http://localhost:3301/telemetry-proxy/` SigNoz предложит
   создать первый аккаунт — это **отдельный логин SigNoz** (не аккаунт CCS), у каждого
   инстанса свой.
3. **Включить в своём `appsettings.Local.json`** (машинно-специфичный, не в гите; образец —
   `appsettings.Local.example.json`):
   ```jsonc
   "Telemetry": {
     // чтобы в SigNoz были данные — CCS слал телеметрию в свой локальный SigNoz
     "Backends": { "Production": { "Enabled": true, "OtlpEndpoint": "http://localhost:4318" } },
     // сам раздел (iframe → локальный SigNoz)
     "Ui": { "Enabled": true, "InternalUrl": "http://localhost:3301" }
   }
   ```
   Без `Backends.Production` раздел откроется, но SigNoz будет пустым.
4. **Роль admin.** Раздел admin-only — пользователь инстанса должен быть `admin` (на своём
   инстансе обычно так и есть).
5. **Рестарт CCS вручную.** `appsettings.Local.json` читается на старте — перезапустить
   процесс (нет трея-оркестратора, поднять тем же способом, что и обычно).
6. **За реверс-прокси/на своём домене** — ничего дополнительного: проброс `/telemetry-proxy`
   относительный, работает на любом origin (в dev — тот же проброс уже прописан в
   `vite.config.ts`).

Опционально, если нужен тот же набор, что на основном инстансе:
- дашборды/представления/правила алертов — накатить на свой SigNoz из репы: `apply.ps1`,
  `apply-views.ps1`, `apply-alerts.ps1` (см. соответствующие доки);
- push-алерты на свой телефон — `Telemetry:Alerts:Enabled=true` + свой SigNoz ApiKey
  (это про доставку алертов, а не про UI-раздел; подписки живут в `data/` этого инстанса).

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
