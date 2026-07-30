# Документация проекта

Карта корпуса: что лежит в каждом разделе и куда класть новое. Вход в проект —
[README.md](../README.md) (что это и как запустить) и [CLAUDE.md](../CLAUDE.md)
(карта кода, инварианты, соглашения); отсюда начинаются детали.

Разделы отвечают на разные вопросы, поэтому папка выбирается по **роли документа**,
а не по теме: одна и та же подсистема может иметь и справочник в `architecture/`,
и инструкцию по эксплуатации в `operations/`.

## Разделы

| Раздел | Отвечает на вопрос | Что внутри |
|---|---|---|
| `architecture/` | как устроено | справочники подсистем — обязательное чтение перед правками кода |
| `observability/` | что видно в проде | телеметрия: обзор, аудит поверхностей, дашборды, развёртывание SigNoz |
| `modules/` | как подключаются внешние модули | контракт «ядро ↔ модуль» и ТЗ на его части |
| `operations/` | как запускать и обслуживать | контейнер, удалённый доступ |
| `design/` | как это выглядит | конвенция дизайна, аудит соответствия макетам, кликабельные прототипы |
| `adr/` | почему решили так | architecture decision records |
| `research/` | что выяснили | исследования и срезы во времени — **не поддерживаются** после написания |
| `omo/` | чужие материалы | переводы промптов oh-my-openagent + правовая рамка |
| `assets/` | картинки и файлы | скриншоты README, тема OnlyOffice |

Ссылки на конкретные документы — ниже: панель «Документация» открывает по ссылке файл,
а папку показать не может, поэтому в таблице они и не ссылки.

## Что где лежит

**architecture/** — [api.md](architecture/api.md) (справочник REST),
[features.md](architecture/features.md) (детали реализованных фич),
[sandbox.md](architecture/sandbox.md) (среда исполнения local/container),
[llm-providers.md](architecture/llm-providers.md),
[mcp-servers.md](architecture/mcp-servers.md),
[knowledge.md](architecture/knowledge.md) (заметки и Dify),
[personas.md](architecture/personas.md),
[spend-analytics-api.md](architecture/spend-analytics-api.md).

**observability/** — [overview.md](observability/overview.md) — главный документ раздела;
[audit.md](observability/audit.md), [dashboards.md](observability/dashboards.md) и
[signoz-setup.md](observability/signoz-setup.md) подчинены ему.

**modules/** — [integration-contract.md](modules/integration-contract.md) — источник правды;
ТЗ ([core-requirements.md](modules/core-requirements.md), [design-kit.md](modules/design-kit.md),
[llm-channel.md](modules/llm-channel.md)) ссылаются на него.

**operations/** — [docker.md](operations/docker.md) (сборка и запуск в контейнере),
[remote-access.md](operations/remote-access.md) (Tailscale + HTTPS).

**design/** — [guidelines.md](design/guidelines.md) (обязательна для правок UI),
[audit.md](design/audit.md) (сверка реализации с макетами), `mockups/` — кликабельные
прототипы в HTML.

**research/** — материалы с датой: [feature-parity.md](research/feature-parity.md),
[messenger-integration.md](research/messenger-integration.md) (не реализовано),
[roadmap-team-zone.md](research/roadmap-team-zone.md). Читать как «так было тогда».

**omo/** — [adoption.md](omo/adoption.md) (правовая рамка), `translations/` — переводы
промптов, из которых генерируются `Services/Prompts/OmoPrompts*.cs`.

## Куда класть новое

- Описание подсистемы, которое надо прочитать перед правкой её кода → `architecture/`,
  плюс короткая выжимка со ссылкой в `CLAUDE.md`.
- Инструкция «как развернуть/настроить/починить» → `operations/`.
- Разбор вариантов, прототип, оценка чужого API → `research/` или `design/mockups/`.
- Принятое архитектурное решение с альтернативами и последствиями → `adr/`,
  имя `ADR-NNN-краткая-суть.md` латиницей.
- Картинка для документа → `assets/`; относительные ссылки на неё панель «Документация»
  и GitHub резолвят одинаково.

Правило про объём — в [CLAUDE.md](../CLAUDE.md): крупное описание живёт отдельным файлом
здесь, а в `CLAUDE.md` попадает выжимка со ссылкой.
