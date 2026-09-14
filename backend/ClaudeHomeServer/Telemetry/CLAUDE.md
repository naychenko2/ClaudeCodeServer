# Observability (OpenTelemetry)

> Этот файл — вынесенная часть корневого `CLAUDE.md`: он загружается, только когда идёт работа с файлами этой папки.

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
