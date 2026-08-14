# SigNoz: развёртывание и настройка

## Обзор

**SigNoz** — open-source APM (Application Performance Monitoring) поверх ClickHouse.
В ClaudeHomeServer используется как **production backend** для OpenTelemetry-телеметрии
(traces, metrics, logs).

Альтернатива для dev-режима — Aspire Dashboard (in-memory, для живого дебага).

## Топология

```
ClaudeHomeServer (ASP.NET Core)
    │
    │ OTLP/gRPC :4317 (dev mode → Aspire)
    │ OTLP/HTTP :4318 (production mode → SigNoz)
    │
    ▼
┌─────────────────────────────────────────────────────────────────────┐
│ SigNoz stack (docker/observability/)                                 │
│                                                                      │
│  ingester ─────────────────────────► ClickHouse ◄── Keeper           │
│     ▲                                     ▲                          │
│     │                                     │                          │
│  OTLP :4317/4318                    signoz (UI+API) :3301            │
│                                           │                          │
│                                           ▼                          │
│                                    Postgres (метастор:               │
│                                    дашборды, юзеры, алерты)          │
└─────────────────────────────────────────────────────────────────────┘
```

Вендоренный compose (`docker/observability/compose.yaml`) поднимает 7 сервисов —
в v0.134 раскладка другая, чем в v0.71: UI и API живут в одном контейнере,
коллектор называется ingester, метастор переехал на Postgres, ZooKeeper заменён
на ClickHouse Keeper.

| Контейнер | Роль |
|---|---|
| `signoz-signoz-0` | UI + API (в v0.71 это была пара frontend + query-service) |
| `signoz-ingester-1` | приём OTLP на :4317/:4318 (бывший otel-collector) |
| `signoz-telemetrystore-clickhouse-0-0` | ClickHouse — traces/metrics/logs |
| `signoz-telemetrykeeper-clickhousekeeper-0` | ClickHouse Keeper (вместо ZooKeeper) |
| `signoz-metastore-postgres-0` | Postgres — дашборды, пользователи, алерты |
| `signoz-telemetrystore-migrator` | миграции схемы ClickHouse, отрабатывает и выходит |
| `signoz-telemetrystore-clickhouse-user-scripts` | histogram-binary для quantile, отрабатывает и выходит |

Два последних штатно в статусе `Exited (0)` — это одноразовые job'ы, а не сбой.

## Первый запуск

### 1. Запуск stack

Запускать ТОЛЬКО через overlay — он подключает вендорный compose через `include`
и биндит порты к `127.0.0.1`:

```powershell
docker compose -f docker-compose.observability.yml up -d
```

> **Не запускать `docker/observability/compose.yaml` напрямую.** Без overlay UI
> сядет на `0.0.0.0:8080` — это порт self-hosted Dify на этой машине, — а OTLP
> встанет на все интерфейсы.

Образы (версии запинены намеренно, см. комментарий в compose.yaml):
- `signoz/signoz:v0.134.0`
- `signoz/signoz-otel-collector:v0.144.6`
- `clickhouse/clickhouse-server:25.12.5`
- `clickhouse/clickhouse-keeper:25.12.5`
- `postgres:16`

### 2. Проверка запуска

```powershell
docker compose -f docker-compose.observability.yml ps

# Логи UI/API и приёмника
docker logs -f signoz-signoz-0
docker logs -f signoz-ingester-1

# Версия работающего сервера
docker exec signoz-signoz-0 /root/signoz --version
```

### 3. Первый setup wizard

Открыть http://localhost:3301/telemetry-proxy/ в браузере (с v0.134 UI и API живут
под base-path из `SIGNOZ_GLOBAL_EXTERNAL__URL`; корень `:3301/` отдаёт 404 — это норма):
1. Создать admin user (email + пароль)
2. Выбрать организацию (default: `My Organization`)
3. Готово — UI пустой, данные появятся после первого хода чата в ClaudeHomeServer

## Retention (срок хранения)

### Дефолтные TTL в ClickHouse

Фактические TTL на v0.134 (сняты с работающего стека — прежняя запись про
«30 дней traces / 90 дней metrics» относилась к v0.71 и не соответствует
действительности):

| Данные | Таблица | TTL |
|---|---|---|
| Traces | `signoz_traces.signoz_index_v3` | **15 дней** (1 296 000 с) |
| Metrics | `signoz_metrics.samples_v4` | **30 дней** (2 592 000 с) |
| Logs | `signoz_logs.logs_v2` | задаётся колонкой `_retention_days` |

### Проверка текущих TTL

```powershell
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
  "SELECT database, name, extract(engine_full, 'TTL[^S]*') AS ttl
   FROM system.tables
   WHERE name IN ('signoz_index_v3','samples_v4','logs_v2') FORMAT Vertical"
```

### Изменение TTL

Через SigNoz UI → Settings → Retention Period — так правится и связанная
метаинформация. Прямой SQL — только как аварийный вариант:

```sql
ALTER TABLE signoz_traces.signoz_index_v3
  MODIFY TTL toDateTime(timestamp) + INTERVAL 30 DAY;
ALTER TABLE signoz_metrics.samples_v4
  MODIFY TTL toDateTime(unix_milli / 1000) + INTERVAL 90 DAY;
```

### Объём диска

Расчёт для одного инстанса ClaudeHomeServer (см. `docs/observability/audit.md`):

| Тип данных | 30 дней | 90 дней |
|---|---|---|
| Traces | ~50 MB | ~150 MB |
| Metrics | ~110 MB | ~330 MB |
| Logs | ~50 MB | ~150 MB |
| ClickHouse overhead | +30% | +30% |
| **Итого** | **~300 MB** | **~820 MB** |

Для multi-instance (dev+prod на одной машине, общий SigNoz): умножить на 2-3.

### Проверка занятого места

```powershell
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
  "SELECT database, table, formatReadableSize(sum(bytes_on_disk)) AS size
   FROM system.parts WHERE active
   GROUP BY database, table ORDER BY sum(bytes_on_disk) DESC LIMIT 10"
```

Ориентир с этого стека: около **63 MiB** за первые сутки работы одного инстанса
(включая служебные таблицы) — то есть таблица выше скорее завышает.

## Порты

| Порт | Сервис | Назначение |
|---|---|---|
| **3301** | `signoz-signoz-0` | SigNoz UI + API (проброс на :8080 контейнера) |
| **4317** | `signoz-ingester-1` | OTLP gRPC — от ClaudeHomeServer |
| **4318** | `signoz-ingester-1` | OTLP HTTP — от ClaudeHomeServer |

Overlay (`docker-compose.observability.yml`) bind'ит все 3 порта к `127.0.0.1` —
внешний доступ отсутствует без reverse-proxy. Это сознательное security-решение:
телеметрия и UI содержат операционные данные.

### Конфликт с другими сервисами

| Порт | Занят чем? | Конфликт? |
|---|---|---|
| 80 | ClaudeHomeServer prod deploy | ❌ |
| 8080 | Dify RAG | ❌ |
| 5000 | ClaudeHomeServer dev | ❌ |
| 5173 | frontend Vite dev | ❌ |
| 8090 | OnlyOffice | ❌ |
| 3301/4317/4318 | SigNoz | ✅ свободно |

## Сеть

- `signoz-network` — изолированная bridge-сеть SigNoz
- НЕ шарится с `cc-sandbox` или app network
- ClaudeHomeServer подключается к SigNoz через host network (`localhost:4317/4318`)

> Внутри `signoz-network` ClickHouse доступен пользователю `default` без пароля,
> а у Postgres дефолтные креды. Наружу портов нет, поэтому для локального стека
> это приемлемо — но не подключать к этой сети посторонние контейнеры.

## Backup

Тома v0.134 (прежняя инструкция архивировала `signoz-clickhouse`, которого
не существует — `docker run -v` молча создал бы пустой том, и «бэкап» получился бы
пустым):

| Том | Что внутри | Критичность |
|---|---|---|
| `signoz-metastore-postgres-0-data` | **дашборды, пользователи, алерты** | высокая |
| `signoz-telemetrystore-0-0-data` | ClickHouse: traces/metrics/logs | средняя (данные и так истекают по TTL) |
| `signoz-telemetrykeeper-0-data` | состояние Keeper | низкая |
| `signoz-telemetrystore-user-scripts` | histogram-binary | низкая, восстановится сама |

**Дашборды бэкапить не нужно** — они лежат в репе как код
(`docker/observability/dashboards/*.json`) и накатываются `apply.ps1`. Это и есть
основной механизм восстановления; см. [dashboards.md](dashboards.md).

Метастор (пользователи, алерты) — через `pg_dump`, а не копированием файлов тома:
у работающей СУБД снимок файлов даёт неконсистентный результат.

```powershell
# Дамп метастора
docker exec signoz-metastore-postgres-0 pg_dump -U signoz -d signoz `
  | Out-File -Encoding utf8 "signoz-metastore-$(Get-Date -Format 'yyyyMMdd').sql"

# Восстановление
Get-Content "signoz-metastore-YYYYMMDD.sql" `
  | docker exec -i signoz-metastore-postgres-0 psql -U signoz -d signoz
```

Данные телеметрии (ClickHouse) при необходимости — штатным `BACKUP` ClickHouse
либо копированием тома при ОСТАНОВЛЕННОМ контейнере.

**Важно:** тома SigNoz живут ВНЕ каталога `data/` проекта, поэтому стандартный
бэкап ClaudeHomeServer (`BackupCore`) их **не покрывает**.

## Troubleshooting

### UI SigNoz не открывается на :3301

0. Открыт правильный путь? UI живёт на `http://localhost:3301/telemetry-proxy/` —
   корень `:3301/` с v0.134 отвечает 404 (весь сервер под base-path).
1. Overlay применился? `docker compose -f docker-compose.observability.yml ps` →
   `signoz-signoz-0` должен слушать `127.0.0.1:3301`
2. Логи: `docker logs signoz-signoz-0`
3. Образ не докачался (network/proxy): `docker compose -f docker-compose.observability.yml pull`, затем `up -d`

### Данные не появляются после хода чата

Порядок важен — он идёт от «данных нет вообще» к «данные есть, но не видны»:

1. **Панель «Пульс телеметрии»** на дашборде «Здоровье сервера». Тикает — pipeline жив,
   и проблема в запросе/диапазоне, а не в экспорте.
2. **Долетает ли хоть что-то в ClickHouse:**
   ```powershell
   docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
     "SELECT toDateTime(intDiv(max(unix_milli),1000)) FROM signoz_metrics.distributed_samples_v4"
   ```
3. **Включён ли экспорт у ТОГО инстанса, что реально запущен.** У боевого деплоя
   (`C:\deploy\claude`) свой `appsettings.Local.json`, и секции `Telemetry` там может
   не быть вовсе — тогда экспортёр не регистрируется и данные не идут ниоткуда.
   Проверить `Telemetry:Backends:Production:Enabled` и `OtlpEndpoint`
   (`http://localhost:4318`), а также что не выставлена `CCS_TELEMETRY_DISABLED=1`.
4. **Приёмник получает данные:** `docker logs signoz-ingester-1`
5. **Миграции схемы прошли:** `docker logs signoz-telemetrystore-migrator`
   (контейнер штатно в `Exited (0)`)
6. **Диапазон в пикере** — данные могут быть старше выбранного окна.

### ClickHouse OOM

1. `docker stats signoz-telemetrystore-clickhouse-0-0`
2. Ограничить память сервису в overlay (`mem_limit`)
3. Снизить retention (см. выше), чтобы данные не копились бесконечно

### Мигратор схемы падает

1. ClickHouse healthy? `docker compose -f docker-compose.observability.yml ps`
2. Логи: `docker logs signoz-telemetrystore-migrator`
   (в норме контейнер отрабатывает и остаётся в `Exited (0)`)
3. Типичная причина — сменилась версия SigNoz. Полный сброс — крайняя мера:
   ```powershell
   docker compose -f docker-compose.observability.yml down -v
   # ВНИМАНИЕ: -v удаляет тома. Пропадут и метастор (пользователи, алерты),
   # и вся телеметрия. Дашборды переживут — они в репе, накатятся apply.ps1.
   docker compose -f docker-compose.observability.yml up -d
   ```

### Коллектор ругается на unset environment variable

Сообщение вида «Configuration references unset environment variable» в логах
`signoz-ingester-1` означает, что `ingester/ingester.yaml` ссылается на переменную,
которой нет в `environment` сервиса. Сейчас коллектор стартует (пустая строка
трактуется как значение по умолчанию), но поведение не задано контрактом
и сломается на версии, ужесточившей парсинг. Добавить переменную явно в compose.

## Чистка мусорных данных

Отладка телеметрии оставляет в базе мусор: тестовые GUID'ы в тегах, значения из
кода, который потом починили (PII в пути, пустой `tool_name`, `model: unknown`).
TTL их уберёт сам, но **до этого они портят не графики, а выпадашки и каталог метрик** —
то есть подсказывают несуществующие значения тому, кто разбирает инцидент.

### Где что лежит

| Таблица | Что внутри | Движок | Мутировать? |
|---|---|---|---|
| `signoz_metrics.samples_v4` | точки метрик | `ReplicatedMergeTree` | да |
| `signoz_metrics.time_series_v4` | ряды и их labels (каталог метрик) | `ReplicatedReplacingMergeTree` | да |
| `signoz_traces.signoz_index_v3` | спаны | `ReplicatedMergeTree` | да |
| `signoz_traces.tag_attributes_v2` | **значения атрибутов для автокомплита** | `ReplicatedReplacingMergeTree` | да |
| `distributed_*` | вьюхи поверх локальных | `Distributed` | **нет**, только читать |

Мусор живёт в двух местах сразу: в самих спанах/сэмплах и отдельно в
`tag_attributes_v2`. Вычистишь только спаны — значение останется в выпадашке.

### Порядок

Всегда три шага; второй без первого — путь к потере живых данных.

```powershell
# 1. Dry-run: ровно тот же WHERE, что пойдёт в DELETE
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
  "SELECT tag_key, string_value, count() FROM signoz_traces.tag_attributes_v2
   WHERE tag_key = 'tool_name' AND trim(string_value) = ''
   GROUP BY 1,2 FORMAT TSV"

# 2. Мутация — синхронно, иначе вернётся до применения
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
  "ALTER TABLE signoz_traces.tag_attributes_v2 DELETE
   WHERE tag_key = 'tool_name' AND trim(string_value) = ''
   SETTINGS mutations_sync = 2"

# 3. Верификация — по DISTINCT значениям, НЕ по count()
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query `
  "SELECT DISTINCT string_value FROM signoz_traces.tag_attributes_v2
   WHERE tag_key = 'tool_name' ORDER BY 1 FORMAT TSV"
```

### Грабли

- **Обратный слеш в литерале — escape-последовательность.** `WHERE string_value =
  'C:\Users\depec\...'` не совпадёт ни с чем: ClickHouse прочтёт `\U` и `\d` как escape.
  Это тихий промах — запрос отработает и вернёт ноль строк, из чего легко сделать вывод
  «чистить нечего». Матчить подстрокой: `position(string_value, 'AppData') > 0`.
- **Пустое значение может быть непустой строкой.** Фильтр `length(value) < 3` пропустит
  значение из трёх пробелов. Правильно — `trim(value) = ''`.
- **`count()` после мутации не измеряет результат.** У `ReplacingMergeTree` мутация
  провоцирует мерж, и число строк падает само по себе за счёт схлопывания дублей
  (в одну из чисток: 827 → 413 при 12 удалённых значениях). Проверять надо составом
  `DISTINCT`, иначе решишь, что снёс лишнее.
- **Мутировать только локальные таблицы.** `ALTER … DELETE` по `distributed_*` — не тот
  объект; вьюха мутации не переживает осмысленно.
- **Сначала посмотреть, что рядом.** Предикат вроде «пустое значение» задевает и легитимные
  теги: в одной из чисток под тот же шаблон попадали 42 валидных пустых значения других
  атрибутов. Поэтому dry-run группируется по `tag_key`, а не только считает строки.

> Чистка — операция для мусора, попавшего по нашей же ошибке. Штатное удаление данных —
> это TTL (см. «Retention»); руками туда лезть не надо.

## Обновление SigNoz

Текущая версия — **v0.134.0** (коллектор v0.144.6), теги запинены в
`docker/observability/compose.yaml`.

Порядок обновления:

1. Поменять теги образов в `compose.yaml` (и `signoz`, и `signoz-otel-collector` —
   их версии независимы).
2. `docker compose -f docker-compose.observability.yml pull`
3. `docker compose -f docker-compose.observability.yml up -d` — мигратор применит
   миграции схемы сам.

> **Откат невозможен.** Мигратор меняет схему ClickHouse необратимо: понизить
> версию после запуска нельзя. Перед мажорным обновлением снять дамп метастора
> (см. «Backup») и читать release notes SigNoz.

## Вендоренные файлы

`docker/observability/` содержит (раскладка сгенерирована Foundry):

```
compose.yaml                           # главный compose (SigNoz v0.134)
ingester/
  ├── ingester.yaml                    # пайплайны и процессоры приёмника OTLP
  └── opamp.yaml                       # OpAMP-конфиг
telemetrystore/clickhouse/
  ├── config-0-0.yaml                  # конфиг ClickHouse
  └── functions.yaml                   # кастомные SQL-функции (histogram-quantile)
telemetrykeeper/clickhousekeeper/
  └── keeper-0.yaml                    # конфиг ClickHouse Keeper
dashboards/                            # дашборды как код + apply.ps1
.signoz-credentials.example.ps1        # шаблон для ключа API (реальный — в .gitignore)
```

Источник: https://github.com/SigNoz/signoz/tree/v0.134.0/deploy/
