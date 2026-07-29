# SigNoz-дашборды ClaudeHomeServer

Дашборды хранятся в репе как JSON (`docker/observability/dashboards/*.json`) —
source of truth. В SigNoz они заливаются идемпотентным скриптом
[`apply.ps1`](../docker/observability/dashboards/apply.ps1) через REST API. Так
переустановка SigNoz или `docker compose down -v` не теряет конфигурацию: один
запуск скрипта возвращает всё на место.

## Состав

| Дашборд | Файл | Назначение |
|---|---|---|
| Здоровье сервера | `llm-operations.json` | Живость сервера, скорость и отказы ходов LLM, вызовы MCP, сессии и соединения |

> **Переименование дашборда плодит дубль.** `apply.ps1` ищет существующий дашборд
> **по title** — сменив заголовок в JSON, следующий прогон создаст НОВЫЙ, а прежний
> останется висеть. Переименовывать надо на месте, по id:
> `curl -X PUT $SIGNOZ_URL/api/v1/dashboards/{id} --data-binary @llm-operations.json`,
> и только потом гонять `apply.ps1` (он уже найдёт дашборд по новому имени).

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

**Как выверить схему, не гадая:** прогнать запрос панели через API и посмотреть,
вернутся ли ряды. Проверять надо через **`/api/v5/query_range`** — у него своя форма
и запроса, и ответа:

```powershell
# spec собирается из полей панели: metricName ← aggregateAttribute.key,
# groupBy ← groupBy (но ключ зовётся name, а не key), filter ← filter.expression
$body = @{
  schemaVersion = 'v1'; start = <ms>; end = <ms>; requestType = 'time_series'
  compositeQuery = @{ queries = @(@{ type = 'builder_query'; spec = @{
    name = 'A'; signal = 'metrics'
    aggregations = @(@{ metricName = 'ccs.llm.duration.count'
                        temporality = 'Cumulative'; timeAggregation = 'rate'; spaceAggregation = 'sum' })
    groupBy = @(@{ name = 'deployment.environment'; fieldDataType = 'string' })
  } }) }
} | ConvertTo-Json -Depth 30
```

Ряды лежат глубже, чем в легаси-API: `data.data.results[].aggregations[].series[]`
(в v3/v4 было `data.result[].series`).

> **Легаси `/api/v4/query_range` наши панели уже не принимает.** С переходом на
> фильтры-выражения (`filter.expression`, нужные дашборд-переменной) `having` стал
> объектом `{expression:""}`, а v4 ждёт там массив и падает с
> `cannot unmarshal object into Go struct field … of type []v3.Having`. Прежний рецепт
> «положить `queryData[0]` в `builderQueries.A`» мёртв — v4 остаётся живым эндпоинтом,
> но для схемы, которой у нас больше нет.

> **Ловушка проверки: `$environment` вручную не подставляется.** Переменные раскрывает
> UI, а не API. Пошлёшь `filter.expression` с `$environment` как есть — получишь
> `status: success` и **ноль рядов**, и решишь, что панель сломана. При ручной проверке
> фильтр либо убирают, либо пишут литералом: `deployment.environment IN ['dev']`.
> Проверено на «Пульсе телеметрии»: без фильтра — 2 ряда, с `$environment` — 0,
> с литералом — 1.

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

### Секции: виджет `row` + `panelMap`

Панели группируются секциями — сворачиваемыми полосами-заголовками. Устроено это
двумя частями, и работает только когда есть обе:

1. **Виджет-заголовок** в `widgets` — всего четыре поля, ни запроса, ни оформления:
   ```jsonc
   { "id": "<uuid>", "panelTypes": "row", "title": "Сбои", "description": "" }
   ```
   В `layout` он занимает всю ширину и высоту 1: `{ h: 1, maxH: 1, minH: 1, minW: 12, w: 12, x: 0, y }`.

2. **Ключ верхнего уровня `panelMap`** — принадлежность панелей секции:
   ```jsonc
   "panelMap": { "<rowId>": { "collapsed": false, "widgets": [ /* записи layout панелей секции */ ] } }
   ```
   Записи здесь дублируют элементы `layout` соответствующих панелей. Без `panelMap`
   заголовки отрисуются, но сворачивать им будет нечего — панели окажутся ничьими.

Координаты в `layout` идут сквозной нумерацией: заголовок секции на `y`, её панели на
`y+1`, следующий заголовок — за самой высокой панелью секции.

> Схему проще всего снять с живого дашборда: добавить секцию в UI и вычитать
> `GET /api/v1/dashboards/{id}`. Так она и была получена — по коду фронтенда судить
> нельзя: тип `row` объявлен в бандле отдельным enum'ом и на первый взгляд выглядит
> неиспользуемым, хотя работает.

### Дев и бой пишут в один SigNoz

На машине живут ДВА инстанса продукта — дев и боевой на порту 80, — и оба шлют в один
SigNoz. Различает их только тег `deployment.environment` (`Telemetry:Mode`): `service.name`
у них общий, а `service.instance.id` равен имени машины, то есть тоже совпадает.

Поэтому `deployment.environment` входит в `groupBy` и легенду **каждой** панели. Без него
p95 длительности хода становится средним по больнице, а всплеск ошибок — ничьим: понять,
дев это экспериментирует или боевой инстанс отваливается, по графику нельзя.

Сверх разреза по рядам есть **дашборд-переменная `environment`** — выпадашка вверху,
которой контур выбирают руками (по умолчанию выбраны все). Разрез отвечает на вопрос
«где именно всплеск», переменная — «покажи мне только боевой»; одно другого не заменяет.

Устроена она двумя частями:

1. **Ключ верхнего уровня `variables`** — объект, где **ключ равен `id` переменной**
   (не имени!), а значение описывает её:
   ```jsonc
   "variables": { "<uuid>": {
     "id": "<тот же uuid>", "name": "environment", "type": "QUERY",
     "queryValue": "SELECT DISTINCT JSONExtractString(labels, 'deployment.environment') ...",
     "multiSelect": true, "showALLOption": true, "allSelected": true, "sort": "ASC", "order": 0,
     "customValue": "", "textboxValue": "", "modificationUUID": "<uuid>"
   } }
   ```
   Тип `QUERY` означает, что список значений SigNoz берёт из ClickHouse сам — контуры
   не захардкожены, новый инстанс появится в выпадашке без правки JSON. Запрос ходит
   в `signoz_metrics.time_series_v4` и фильтрует по `metric_name LIKE 'ccs.%'`, чтобы
   в список не подмешались чужие сервисы.

2. **Фильтр в КАЖДОЙ панели** — `filter.expression`:
   ```jsonc
   "filter": { "expression": "deployment.environment IN $environment" }
   ```
   Пропустишь в одной панели — она молча останется общей по обоим контурам,
   и на дашборде это выглядит не как ошибка, а как расхождение данных.

> Раньше здесь стояло «переменную не заводим: схему не выверить, а неверная ломает
> фильтр во всех панелях сразу». Опасение верное по механике, но вывод был неправильный:
> схема снимается с живого дашборда (`GET /api/v1/dashboards/{id}` после ручного
> добавления переменной в UI) — ровно так же, как схема секций. Не проверяется только
> то, что не пробовали проверить.

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
4. Прогони запрос панели через `/api/v5/query_range` — убедись, что панель не пуста
   (см. «Схема JSON» выше). Успешный импорт этого НЕ гарантирует
5. (Опц.) Добавь строку в таблицу «Состав» выше

## Если дашборд пустой — порядок проверки

**Сначала отдели «пусто» от «нечего показывать»** — иначе диагноз будет неверным:

> **Пустой счётчик — это норма, а не поломка.** Счётчики (`ccs.llm.errors`,
> `ccs.dify.sync.errors`, `ccs.mcp.errors`) кумулятивные: до первого инкремента ряда
> **не существует вовсе** — экспортировать нечего, — и сбрасываются они с перезапуском
> процесса. Поэтому пустая панель отказов читается как «с момента старта сбоев не было»,
> а вовсе не как «метрика сломана». Отличить одно от другого можно по каталогу метрик:
> ```powershell
> docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
>   "SELECT DISTINCT metric_name FROM signoz_metrics.time_series_v4 WHERE metric_name LIKE 'ccs.%' ORDER BY 1"
> ```
> Метрика в каталоге есть, а ряды пусты → инструмент жив, событий не было. Метрики нет
> вовсе → её ни разу не инкрементили за всё время хранения.

Дальше — по нарастанию «данные не доехали» → «доехали, но не видны»:

1. **Панель «Пульс телеметрии»** — если она в нуле, стоит не приложение, а экспорт
   телеметрии; остальные панели пусты потому, что данные не доехали.
2. **Идут ли данные вообще:**
   `docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query "SELECT max(unix_milli) FROM signoz_metrics.distributed_samples_v4"`
3. **Включён ли экспорт у приложения** — секция `Telemetry` в `appsettings.Local.json`
   ТОГО инстанса, что реально запущен (у боевого деплоя свой файл), и не выставлена ли
   переменная `CCS_TELEMETRY_DISABLED=1`.
4. **Выбранный контур** — выпадашка `environment` вверху дашборда. Если выбран только
   `prod`, а пишет сейчас дев, пусто будет во всех панелях сразу.
5. **Схема запроса панели** — прогнать через `/api/v5/query_range` (при ручной проверке
   `$environment` заменить литералом, см. «Схема JSON»).
6. **Диапазон времени** в пикере — данные могут быть старше выбранного окна.

## Cross-links

- [Observability overview](observability.md) — общая архитектура OTel-стека
- [SigNoz setup](observability-signoz-setup.md) — развёртывание SigNoz, troubleshooting
