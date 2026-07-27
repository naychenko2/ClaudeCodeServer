# Vendored SigNoz docker-compose

**Версия:** v0.71.0 (последняя стабильная с классическим docker-compose).

**Источник:** https://github.com/SigNoz/signoz/blob/v0.71.0/deploy/docker/

## Зачем вендорить

SigNoz upstream хранит конфиги в нескольких папках с относительными путями вида
`../common/`. Для self-contained развёртывания вендорим всё в одну папку — нет
внешних зависимостей.

## Запуск

НЕ запускать напрямую из этой папки. Использовать overlay:

```powershell
docker compose `
  -f docker/observability/docker-compose.yaml `
  -f docker-compose.observability.yml `
  up -d
```

Overlay добавляет bind портов к localhost и опциональные resource limits.

## Что внутри

```
docker-compose.yaml                       # главный compose
clickhouse/
  ├── config.xml                          # конфиг ClickHouse
  ├── users.xml                           # пользователи ClickHouse
  ├── cluster.xml                         # кластерная конфигурация
  └── custom-function.xml                 # кастомные SQL-функции
signoz/
  ├── prometheus.yml                      # конфиг query-service (scrape targets)
  ├── dashboards/.gitkeep                 # встроенные дашборды
  ├── otel-collector-opamp-config.yaml    # OpAMP-конфиг
  └── nginx-config.conf                   # конфиг frontend nginx
otel-collector-config.yaml                # конфиг otel-collector
```

## Обновление

См. `docs/observability-signoz-setup.md` → «Обновление SigNoz».

## Источник правды

- Версия: `0.71.0` (DOCKER_TAG в docker-compose.yaml)
- Release notes: https://github.com/SigNoz/signoz/releases/tag/v0.71.0
- Migration guide (для крупных обновлений): https://signoz.io/docs/migration/
