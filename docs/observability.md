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
  [observability-signoz-setup.md](observability-signoz-setup.md#retention-срок-хранения)).
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
- **Развёртывание и troubleshooting:** [docs/observability-signoz-setup.md](observability-signoz-setup.md)

### Aspire Dashboard (dev, optional)

- **Хранилище:** in-memory, окно трейсов ~110 минут.
- **Live debugging UI** без настройки персистентности.
- **Setup:** отдельный docker-compose (TODO когда добавим).

## Duplication Architecture

Подробный аудит существующих поверхностей: [docs/observability-audit.md](observability-audit.md).

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

**Allowlist тегов:** `{provider, model, direction, tool_name, outcome, error_type, reason}`.

Все значения тегов — из замкнутых enum-множеств, не свободный текст. Это защищает
ClickHouse от взрыва кардинальности (миллионы уникальных тегов = деградация запросов).

**Тест-страж:** `backend/ClaudeHomeServer.Tests/Telemetry/MetricTagAllowlistTests.cs` —
отказывается компилироваться / падает при попытке добавить forbidden tag.

## Sampling Strategy

Сэмплинг на стороне приложения (до экспорта), конфигурируется через
`Telemetry:TraceSampleRatio:{Dev,Production}`.

| Режим | Sampler | Ratio | Обоснование |
|---|---|---|---|
| `dev` | `ParentBased(TraceIdRatio(0.10))` | 10% | Расширяет Aspire window с ~110 мин до ~18 ч видимости |
| `production` | `ParentBased(TraceIdRatio(0.05))` | 5% | Ниже overhead для sustained prod-трафика |

`ParentBased` гарантирует, что дочерние спаны следуют решению родителя — распределённый
трейс не обрывается на середине.

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

## Cross-links

- [Аудит существующих поверхностей](observability-audit.md) — что уже есть в проекте, чтобы
  не дублировать. Сводная таблица 4 сторов + cross-reference планируемых OTel-метрик.
- [SigNoz setup runbook](observability-signoz-setup.md) — развёртывание stack, retention,
  порты, troubleshooting, объём диска.
- [SigNoz-дашборды](observability-dashboards.md) — дашборды как IaC: JSON в репе,
  идемпотентный импорт через `apply.ps1`, backup-стратегия.
- [MCP-servers docs](mcp-servers.md) — диагностика MCP-вызовов через `GET /api/mcp/calls`
  (in-memory счётчики, дополняют OTel).
- [CLAUDE.md](../CLAUDE.md) — общая архитектура проекта, REST API, соглашения.

## Future Epics (explicitly deferred)

Эти элементы сознательно вынесены за scope текущего плана. Каждый — отдельный epic со
своим ADR.

1. **Alerting** — SigNoz email/Telegram alerts: LLM error rate, heartbeat absent,
   p99 duration threshold. Требует определения SLO и канала доставки.
2. **WebDavHandler instrumentation** — вне MVC pipeline, нужен custom `IMeter` wrapper
   вокруг WebDav-обработчиков. Отдельная проработка surface area.
3. **Persistent postmortem** — если SigNoz + ClickHouse не хватит для deep-dive расследований,
   Tempo/Loki stack. Сейчас N/A: 30d traces уже персистентны.
4. **End-user UI** — если понадобится user-facing observability (не только админ).
   Требует privacy review: текущий scope = admin only, per-user attribution отключена.
