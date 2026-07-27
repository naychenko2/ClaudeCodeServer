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
│  otel-collector ──► query-service ──► ClickHouse                     │
│       ▲                 │                  │                         │
│       │                 ▼                  │                         │
│  OTLP :4317/4318    frontend:3301     signoz-clickhouse:/var/lib/... │
│                                          (persistent volume)          │
└─────────────────────────────────────────────────────────────────────┘
```

Вендоренный compose в `docker/observability/` содержит 9 сервисов:
- `init-clickhouse` — загружает histogram-binary для quantile-агрегаций
- `zookeeper` — координация ClickHouse (кластерный режим)
- `clickhouse` — основное хранилище (WAL, persistent volume)
- `alertmanager` — manage alerts (пока не настраиваем — отдельный epic)
- `query-service` — SigNoz API поверх ClickHouse
- `frontend` — UI на :3301 (nginx + React)
- `otel-collector` — приём OTLP на :4317/:4318
- `schema-migrator-sync` / `schema-migrator-async` — миграции схемы ClickHouse

## Первый запуск

### 1. Запуск stack

```powershell
docker compose `
  -f docker/observability/docker-compose.yaml `
  -f docker-compose.observability.yml `
  up -d
```

Вендоренный compose тянет образы:
- `clickhouse/clickhouse-server:24.1.2-alpine`
- `bitnami/zookeeper:3.7.1`
- `signoz/query-service:0.71.0`
- `signoz/frontend:0.71.0`
- `signoz/signoz-otel-collector:0.111.26`
- `signoz/alertmanager:0.23.7`
- `signoz/signoz-schema-migrator:0.111.24`

### 2. Проверка запуска

```powershell
# Все сервисы healthy
docker compose -f docker/observability/docker-compose.yaml ps

# Логи query-service (вместе с schema-migrator)
docker logs -f signoz-query-service

# Должен ответить health endpoint
curl http://localhost:3301/api/v1/health
```

### 3. Первый setup wizard

Открыть http://localhost:3301 в браузере:
1. Создать admin user (email + пароль)
2. Выбрать организацию (default: `My Organization`)
3. Готово — UI пустой, данные появятся после первого хода чата в ClaudeHomeServer

## Retention (срок хранения)

### Дефолтные TTL в ClickHouse

SigNoz v0.71.0 настроен на **30 дней traces / 90 дней metrics** через TTL в
таблицах ClickHouse (`signoz_traces.*`, `signoz_metrics.*`).

### Проверка текущих TTL

```sql
-- Подключиться к ClickHouse
docker exec -it signoz-clickhouse clickhouse-client

-- Посмотреть TTL для таблицы traces
SHOW CREATE TABLE signoz_traces.signoz_index_v3;

-- Для метрик
SHOW CREATE TABLE signoz_metrics.samples;
```

### Изменение TTL

Через SigNoz UI → Settings → Retention Period. Или напрямую через SQL:

```sql
ALTER TABLE signoz_traces.signoz_index_v3 MODIFY TTL toDateTime(timestamp) + INTERVAL 60 DAY;
ALTER TABLE signoz_metrics.samples MODIFY TTL toDateTime(timestamp_ms) + INTERVAL 180 DAY;
```

### Объём диска

Расчёт для одного инстанса ClaudeHomeServer (см. `docs/observability-audit.md`):

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
# Размер volume
docker volume inspect signoz-clickhouse --format '{{.Mountpoint}}'
# На Windows Docker Desktop: обычно в WSL2 VHDX

# Через ClickHouse
docker exec signoz-clickhouse clickhouse-client -q "
  SELECT
    database,
    table,
    formatBytes(sum(bytes_on_disk)) AS size
  FROM system.parts
  WHERE active
  GROUP BY database, table
  ORDER BY sum(bytes_on_disk) DESC
  LIMIT 10
"
```

## Порты

| Порт | Сервис | Назначение |
|---|---|---|
| **3301** | frontend | SigNoz UI (nginx + React) |
| **4317** | otel-collector | OTLP gRPC — от ClaudeHomeServer |
| **4318** | otel-collector | OTLP HTTP — от ClaudeHomeServer |

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

- `signoz-net` — изолированная bridge сеть SigNoz
- НЕ шарится с `cc-sandbox` или app network
- ClaudeHomeServer подключается к SigNoz через host network (`localhost:4317/4318`)

## Backup

Volume `signoz-clickhouse` содержит все данные ClickHouse. Для бэкапа:

```powershell
# Архивировать volume
docker run --rm `
  -v signoz-clickhouse:/data `
  -v "${PWD}:/backup" `
  alpine tar czf /backup/signoz-data-$(Get-Date -Format "yyyyMMdd").tar.gz /data

# Восстановление
docker run --rm `
  -v signoz-clickhouse:/data `
  -v "${PWD}:/backup" `
  alpine sh -c "rm -rf /data/* && tar xzf /backup/signoz-data-YYYYMMDD.tar.gz -C /"
```

**Важно:** volume `signoz-clickhouse` живёт ВНЕ каталога `data/` проекта, поэтому
он **не покрывается** стандартным бэкапом ClaudeHomeServer (`BackupCore`). Бэкапить
отдельно, по расписанию или вручную.

## Troubleshooting

### UI SigNoz не открывается на :3301

1. Проверить что overlay применился: `docker compose ps` → frontend должен слушать `127.0.0.1:3301`
2. Проверить health frontend: `docker logs signoz-frontend`
3. Сценарий — образ не докачался (network/proxy): `docker compose pull`, затем `up -d` снова

### Данные не появляются после хода чата

1. Проверить `appsettings.Local.json` → `Telemetry:Backends:Production:OtlpEndpoint` указывает на `http://localhost:4318`
2. Проверить что ClaudeHomeServer стартовал без telemetry errors в логах:
   ```powershell
   docker logs claude-server 2>&1 | Select-String "telemetry|otlp|OpenTelemetry"
   ```
3. Проверить что otel-collector получает данные: `docker logs signoz-otel-collector`
4. Проверить что schema-migrator завершился успешно: `docker logs signoz-schema-migrator-sync`

### ClickHouse OOM

1. Проверить memory usage: `docker stats signoz-clickhouse`
2. Раскомментировать `mem_limit: 2g` в `docker-compose.observability.yml`
3. Поднять retention (см. выше), чтобы данные не копились бесконечно

### schema-migrator падает

1. Проверить что ClickHouse healthy: `docker compose ps clickhouse`
2. Логи мигратора: `docker logs signoz-schema-migrator-sync`
3. Типичная причина — изменилась версия SigNoz, нужен полный `down` + `up`:
   ```powershell
   docker compose -f docker/observability/docker-compose.yaml down -v
   # WARNING: -v удаляет volumes, все данные потеряны!
   docker compose -f docker/observability/docker-compose.yaml -f docker-compose.observability.yml up -d
   ```

## Обновление SigNoz

Вендоренная версия — **v0.71.0** (последняя стабильная с классическим compose).
SigNoz v0.130.0+ перешёл на Foundry для деплоя (compose в репе deprecated).

### Обновление в рамках 0.71.x

1. Изменить тег в `docker/observability/docker-compose.yaml`:
   ```yaml
   query-service:
     image: signoz/query-service:${DOCKER_TAG:-0.71.0}  # ← поменять
   ```
2. `docker compose pull && docker compose up -d`
3. Schema-migrator применит миграции автоматически

### Миграция на v0.130.0+ (Foundry)

Отдельная задача — прочитать [migration guide](https://signoz.io/docs/migration/)
перед обновлением. Foundry использует другой механизм деплоя.

## Вендоренные файлы

`docker/observability/` содержит:

```
docker-compose.yaml                    # главный compose (из signoz/signoz v0.71.0)
clickhouse/
  ├── config.xml                       # конфиг ClickHouse
  ├── users.xml                        # пользователи ClickHouse
  ├── cluster.xml                      # кластерная конфигурация
  └── custom-function.xml              # кастомные SQL-функции
signoz/
  ├── prometheus.yml                   # конфиг query-service (scrape targets)
  ├── dashboards/.gitkeep              # встроенные дашборды (пусто — добавляются через UI)
  ├── otel-collector-opamp-config.yaml # OpAMP-конфиг для динамической настройки коллектора
  └── nginx-config.conf                # конфиг frontend nginx
otel-collector-config.yaml             # конфиг otel-collector (pipelines, processors)
```

Источник: https://github.com/SigNoz/signoz/blob/v0.71.0/deploy/docker/
