# SigNoz-дашборды ClaudeHomeServer

Дашборды хранятся в репе как JSON (`docker/observability/dashboards/*.json`) —
source of truth. В SigNoz они заливаются идемпотентным скриптом
[`apply.ps1`](../docker/observability/dashboards/apply.ps1) через REST API. Так
переустановка SigNoz или `docker compose down -v` не теряет конфигурацию: один
запуск скрипта возвращает всё на место.

## Состав

| Дашборд | Файл | Назначение |
|---|---|---|
| LLM Operations | `llm-operations.json` | Здоровье LLM-провайдеров: latency, error rate, rate-limit hits, top моделей |

## Схема JSON: только формат builder v0.134

Виджет описывает запрос так (упрощённо):

```jsonc
"queryType": "builder",              // строка, НЕ число
"query": {
  "queryType": "builder",
  "promQL": [], "clickhouse_sql": [],
  "builder": {
    "queryData": [ { "queryName": "A", "dataSource": "metrics", ... } ],
    "queryFormulas": []
  }
}
```

> **Грабли.** Прежняя версия дашборда была написана в легаси-схеме (`"queryType": 2`
> и блок `metricsBuilder.queryBuilder`). SigNoz такой JSON **принимает и сохраняет
> без ошибок** — он схему не валидирует, — но UI v0.134 читает только
> `query.builder.queryData`, поэтому все панели молча оставались пустыми. Успешный
> импорт ничего не доказывает: проверять надо рендер или запросы.

**Как выверить схему, не гадая:** прогнать запрос виджета через API и посмотреть,
вернутся ли ряды. `queryData[0]` кладётся в `builderQueries.A` почти как есть:

```powershell
$body = '{"start":<ms>,"end":<ms>,"step":300,"compositeQuery":
  {"queryType":"builder","panelType":"graph","builderQueries":{"A": <queryData[0]> }}}'
curl.exe -sS -X POST "$SIGNOZ_URL/api/v4/query_range" `
  -H "SIGNOZ-API-KEY: $env:SIGNOZ_JWT" -H "Content-Type: application/json" --data-binary "@body.json"
```

`status: success` и непустой `data.result[].series` = панель отрисуется.

### `groupBy` — массив, а не объект

Разрезы задаются **массивом** ключей, даже когда ключ один:

```jsonc
"groupBy": [
  { "isColumn": false, "type": "tag", "key": "provider", "isJSON": false, "dataType": "string" },
  { "isColumn": false, "type": "tag", "key": "deployment.environment", "isJSON": false, "dataType": "string" }
]
```

В первой редакции дашборда здесь стоял одиночный объект (а у одной панели — `null`).
SigNoz это проглотил, как глотает любой JSON. Что схема именно массив — проверено
запросом к `/api/v5/query_range`: массив из двух ключей реально возвращает ряды,
разрезанные по обоим (`labels` содержат и `provider`, и `deployment.environment`).

### Дев и бой пишут в один SigNoz

На машине живут ДВА инстанса продукта — дев и боевой на порту 80, — и оба шлют в один
SigNoz. Различает их только тег `deployment.environment` (`Telemetry:Mode`): `service.name`
у них общий, а `service.instance.id` равен имени машины, то есть тоже совпадает.

Поэтому `deployment.environment` входит в `groupBy` и легенду **каждой** панели. Без него
p95 длительности хода становится средним по больнице, а всплеск ошибок — ничьим: понять,
дев это экспериментирует или боевой инстанс отваливается, по графику нельзя.

Дашборд-переменную вместо этого не заводим: SigNoz JSON не валидирует, поэтому схему
переменных не выверить ни импортом, ни ответом API — а неверно собранная переменная ломает
фильтр во всех панелях сразу. Разрез по рядам такого риска не несёт.

### Имена метрик: гистограмма разворачивается в суффиксы

Экспортёр сохраняет OTel-имена как есть (точки не заменяются, `_total` не добавляется),
но гистограмма превращается в несколько метрик. Голого `ccs.llm.duration` в базе **нет**:

| Нужно | Метрика | `type` | `spaceAggregation` |
|---|---|---|---|
| Частота вызовов | `ccs.llm.duration.count` | `Sum` | `sum` |
| Перцентили (p50/p99) | `ccs.llm.duration.bucket` | `Histogram` | `p50` / `p99` |
| Счётчики (ошибки, hits) | `ccs.llm.errors` | `Sum` | `sum` |

## Авторизация в SigNoz API

SigNoz v0.134+ принимает заголовок `SIGNOZ-API-KEY: <token>` (для Service Account
keys и PAT). Старый `Authorization: Bearer <jwt>` работает для JWT из `/api/v1/login`,
но при наличии обоих SigNoz приоритизирует `Authorization`, и PAT падает с 401 —
поэтому `apply.ps1` всегда шлёт `SIGNOZ-API-KEY`, а `Bearer` добавляет только
для значения, похожего на JWT. Два варианта получить токен:

### Вариант 1: Personal Access Token (PAT) — рекомендуется

Долгоживущий токен, создаётся один раз и не требует пароля в скрипте:

1. Открой SigNoz UI — http://localhost:3301 (залогинься admin'ом)
2. **Settings → API Keys → New Key**
3. Скопируй токен (длинная строка)

### Вариант 2: email + пароль

Те же креды, что ты задал при первом запуске SigNoz UI (setup wizard). Скрипт
логинится через `POST /api/v1/login` при каждом запуске, получает короткоживущий JWT.

### Где хранить креды

`docker/observability/.signoz-credentials.ps1` (в `.gitignore`, шаблон
`.signoz-credentials.example.ps1`). apply.ps1 автоматически его dot-source'ит.

```powershell
# .signoz-credentials.ps1 — PAT (рекомендуемый):
$env:SIGNOZ_JWT = "signoz-pat-..."

# или email + пароль:
# $env:SIGNOZ_EMAIL    = "you@example.com"
# $env:SIGNOZ_PASSWORD = "your-password"

# опционально:
# $env:SIGNOZ_URL = "http://localhost:3301"
```

## Применение

Если `.signoz-credentials.ps1` на месте — просто запусти скрипт без параметров:

```powershell
# Из репы
docker\observability\dashboards\apply.ps1
```

Разовые вызовы без файла (для тестов):

```powershell
.\apply.ps1 -Jwt "signoz-pat-..."
.\apply.ps1 -Email "you@example.com" -Password "secret"
.\apply.ps1 -SignozUrl "http://signoz.example.com:3301" -Jwt "..."
```

Скрипт:
- `GET /api/v1/dashboards` — получает список существующих
- Для каждого `*.json` в папке: матч по `title` → `PUT` если есть, `POST` если новый
  (идентификатор дашборда в v0.134 — поле `id`, в v0.71 было `uuid`; поддержаны оба)
- Проверяет HTTP-код ЯВНО (`-w "%{http_code}"`). Одного кода возврата curl мало:
  без `-f` он отдаёт 0 и при 401/403/500, из-за чего протухший ключ раньше выглядел
  как `✓ OK`, хотя в SigNoz ничего не заливалось
- Логирует `✓ OK (HTTP 200)` / `✗ HTTP <код>: <тело ответа>`

## Backup-стратегия

- **JSON в репе** (`docker/observability/dashboards/*.json`) — source of truth,
  версионирование в git.
- **Метастор SigNoz** — с v0.134 это **Postgres** (контейнер `signoz-metastore-postgres-0`,
  том `signoz-metastore-postgres-0-data`), а не SQLite, как было в v0.71. Именно там
  лежат дашборды и пользователи. Переживёт `docker compose stop` / `down` (без `-v`);
  при `down -v` теряется и восстанавливается запуском `apply.ps1`.
- После каждого изменения JSON → коммит в репу → запуск `apply.ps1`.

## Добавление нового дашборда

1. Создай `docker/observability/dashboards/<name>.json` (используй
   `llm-operations.json` как образец — он в актуальной схеме builder)
2. Закоммить в репу
3. Запусти `apply.ps1` (креды подхватятся из `.signoz-credentials.ps1`)
4. Прогони запрос виджета через `/api/v4/query_range` — убедись, что панель не пуста
   (см. «Схема JSON» выше). Успешный импорт этого НЕ гарантирует
5. (Опц.) Добавь строку в таблицу «Состав» выше

## Если дашборд пустой — порядок проверки

1. **Панель «Telemetry Heartbeat»** — если она в нуле, стоит не приложение, а экспорт
   телеметрии; остальные панели пусты потому, что данные не доехали.
2. **Идут ли данные вообще:**
   `docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query "SELECT max(unix_milli) FROM signoz_metrics.distributed_samples_v4"`
3. **Включён ли экспорт у приложения** — секция `Telemetry` в `appsettings.Local.json`
   ТОГО инстанса, что реально запущен (у боевого деплоя свой файл), и не выставлена ли
   переменная `CCS_TELEMETRY_DISABLED=1`.
4. **Схема запроса виджета** — прогнать через `/api/v4/query_range`.
5. **Диапазон времени** в пикере — данные могут быть старше выбранного окна.

## Cross-links

- [Observability overview](observability.md) — общая архитектура OTel-стека
- [SigNoz setup](observability-signoz-setup.md) — развёртывание SigNoz, troubleshooting
